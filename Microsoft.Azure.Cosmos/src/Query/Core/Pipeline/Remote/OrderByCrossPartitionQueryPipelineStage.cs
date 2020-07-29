﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// ------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Query.Core.Pipeline.Remote
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Pagination;
    using Microsoft.Azure.Cosmos.Query.Core.Collections;
    using Microsoft.Azure.Cosmos.Query.Core.ContinuationTokens;
    using Microsoft.Azure.Cosmos.Query.Core.Exceptions;
    using Microsoft.Azure.Cosmos.Query.Core.ExecutionContext.ItemProducers;
    using Microsoft.Azure.Cosmos.Query.Core.ExecutionContext.OrderBy;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// CosmosOrderByItemQueryExecutionContext is a concrete implementation for CrossPartitionQueryExecutionContext.
    /// This class is responsible for draining cross partition queries that have order by conditions.
    /// The way order by queries work is that they are doing a k-way merge of sorted lists from each partition with an added condition.
    /// The added condition is that if 2 or more top documents from different partitions are equivalent then we drain from the left most partition first.
    /// This way we can generate a single continuation token for all n partitions.
    /// This class is able to stop and resume execution by generating continuation tokens and reconstructing an execution context from said token.
    /// </summary>
    internal sealed class OrderByCrossPartitionQueryPipelineStage : CrossPartitionQueryPipelineStage
    {
        private static class Expressions
        {
            public const string LessThan = "<";
            public const string LessThanOrEqualTo = "<=";
            public const string EqualTo = "=";
            public const string GreaterThan = ">";
            public const string GreaterThanOrEqualTo = ">=";
        }

        /// <summary>
        /// Order by queries are rewritten to allow us to inject a filter.
        /// This placeholder is so that we can just string replace it with the filter we want without having to understand the structure of the query.
        /// </summary>
        private const string FormatPlaceHolder = "{documentdb-formattableorderbyquery-filter}";

        /// <summary>
        /// If query does not need a filter then we replace the FormatPlaceHolder with "true", since
        /// "SELECT * FROM c WHERE blah and true" is the same as "SELECT * FROM c where blah"
        /// </summary>
        private const string TrueFilter = "true";

        /// <summary>
        /// For ORDER BY queries we need to drain the first page of every enumerator before adding it to the prioirty queue for k-way merge.
        /// Since this requires async work, we defer it until the user calls MoveNextAsync().
        /// </summary>
        private readonly Queue<(OrderByQueryPartitionRangePageAsyncEnumerator, OrderByContinuationToken)> uninitializedEnumerators;

        private readonly PriorityQueue<OrderByQueryPartitionRangePageAsyncEnumerator> initializedEnumerators;

        private readonly CrossPartitionRangePageAsyncEnumerator<OrderByQueryPage, QueryState> crossPartitionRangePageAsyncEnumerator;

        private readonly List<SortOrder> sortOrders;

        private readonly IDocumentContainer documentContainer;

        private OrderByCrossPartitionQueryPipelineStage(
            CrossPartitionRangePageAsyncEnumerator<OrderByQueryPage, QueryState> crossPartitionRangePageAsyncEnumerator)
        {
            this.crossPartitionRangePageAsyncEnumerator = crossPartitionRangePageAsyncEnumerator ?? throw new ArgumentNullException(nameof(crossPartitionRangePageAsyncEnumerator));
        }

        public TryCatch<QueryPage> Current => throw new NotImplementedException();

        public ValueTask DisposeAsync() => this.crossPartitionRangePageAsyncEnumerator.DisposeAsync();

        public async ValueTask<bool> MoveNextAsync()
        {
            if (this.uninitializedEnumerators.Count != 0)
            {
                (OrderByQueryPartitionRangePageAsyncEnumerator enumerator, OrderByContinuationToken token) = this.uninitializedEnumerators.Dequeue();
                if (token != null)
                {
                    TryCatch monadicFilterAsync = await OrderByCrossPartitionQueryPipelineStage.MonadicFilterAsync(
                        enumerator,
                        this.sortOrders,
                        token,
                        cancellationToken: default);
                    if (monadicFilterAsync.Failed)
                    {
                        // Check if it's a retryable exception.
                        Exception exception = monadicFilterAsync.Exception;
                        while (exception.InnerException != null)
                        {
                            exception = exception.InnerException;
                        }

                        if (IsSplitException(exception))
                        {
                            // Handle split
                            IEnumerable<PartitionKeyRange> childRanges = await this.documentContainer.GetChildRangeAsync(
                                enumerator.Range,
                                cancellationToken: default);
                            foreach (PartitionKeyRange childRange in childRanges)
                            {
                                OrderByQueryPartitionRangePageAsyncEnumerator childPaginator = new OrderByQueryPartitionRangePageAsyncEnumerator(
                                    documentContainer,
                                    sqlQuerySpec,
                                    range,
                                    pageSize,
                                    state: default)
                                enumerators.Enqueue(childPaginator);
                            }

                            // Recursively retry
                            return await this.MoveNextAsync();
                        }
                    }
                }
                else
                {

                }
            }
        }

        public static TryCatch<IQueryPipelineStage> MonadicCreate(
            IDocumentContainer documentContainer,
            SqlQuerySpec sqlQuerySpec,
            IReadOnlyList<PartitionKeyRange> targetRanges,
            IReadOnlyList<OrderByColumn> orderByColumns,
            int pageSize,
            CosmosElement continuationToken)
        {
            // TODO (brchon): For now we are not honoring non deterministic ORDER BY queries, since there is a bug in the continuation logic.
            // We can turn it back on once the bug is fixed.
            // This shouldn't hurt any query results.

            if (documentContainer == null)
            {
                throw new ArgumentNullException(nameof(documentContainer));
            }

            if (sqlQuerySpec == null)
            {
                throw new ArgumentNullException(nameof(sqlQuerySpec));
            }

            if (targetRanges == null)
            {
                throw new ArgumentNullException(nameof(targetRanges));
            }

            if (targetRanges.Count == 0)
            {
                throw new ArgumentException($"{nameof(targetRanges)} must not be empty.");
            }

            if (orderByColumns == null)
            {
                throw new ArgumentNullException(nameof(orderByColumns));
            }

            if (orderByColumns.Count == 0)
            {
                throw new ArgumentException($"{nameof(orderByColumns)} must not be empty.");
            }

            if (pageSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }

            List<OrderByQueryPartitionRangePageAsyncEnumerator> remoteEnumerators;
            if (continuationToken == null)
            {
                // Start off all the partition key ranges with null continuation
                SqlQuerySpec rewrittenQueryForOrderBy = new SqlQuerySpec(
                    sqlQuerySpec.QueryText.Replace(oldValue: FormatPlaceHolder, newValue: TrueFilter),
                    sqlQuerySpec.Parameters);

                remoteEnumerators = targetRanges
                    .Select(range => new OrderByQueryPartitionRangePageAsyncEnumerator(
                        documentContainer,
                        sqlQuerySpec,
                        range,
                        pageSize,
                        state: default))
                    .ToList();
            }
            else
            {
                TryCatch<PartitionMapping<OrderByContinuationToken>> monadicGetOrderByContinuationTokenMapping = MonadicGetOrderByContinuationTokenMapping(
                    targetRanges,
                    continuationToken,
                    orderByColumns.Count);
                if (monadicGetOrderByContinuationTokenMapping.Failed)
                {
                    return TryCatch<IQueryPipelineStage>.FromException(monadicGetOrderByContinuationTokenMapping.Exception);
                }

                PartitionMapping<OrderByContinuationToken> partitionMapping = monadicGetOrderByContinuationTokenMapping.Result;

                IReadOnlyList<CosmosElement> orderByItems = partitionMapping
                    .TargetPartition
                    .Values
                    .First()
                    .OrderByItems
                    .Select(x => x.Item)
                    .ToList();
                if (orderByItems.Count != orderByColumns.Count)
                {
                    return TryCatch<IQueryPipelineStage>.FromException(
                        new MalformedContinuationTokenException(
                            $"Order By Items from continuation token did not match the query text. " +
                            $"Order by item count: {orderByItems.Count()} did not match column count {orderByColumns.Count()}. " +
                            $"Continuation token: {continuationToken}"));
                }

                ReadOnlyMemory<(OrderByColumn, CosmosElement)> columnAndItems = orderByColumns.Zip(orderByItems, (column, item) => (column, item)).ToArray();

                // For ascending order-by, left of target partition has filter expression > value,
                // right of target partition has filter expression >= value, 
                // and target partition takes the previous filter from continuation (or true if no continuation)
                (string leftFilter, string targetFilter, string rightFilter) = OrderByCrossPartitionQueryPipelineStage.GetFormattedFilters(columnAndItems);
                List<(IReadOnlyDictionary<PartitionKeyRange, OrderByContinuationToken>, string)> tokenMappingAndFilters = new List<(IReadOnlyDictionary<PartitionKeyRange, OrderByContinuationToken>, string)>()
                {
                    { (partitionMapping.PartitionsLeftOfTarget, leftFilter) },
                    { (partitionMapping.TargetPartition, targetFilter) },
                    { (partitionMapping.PartitionsRightOfTarget, rightFilter) },
                };

                IReadOnlyList<SortOrder> sortOrders = orderByColumns.Select(column => column.SortOrder).ToList();
                remoteEnumerators = new List<OrderByQueryPartitionRangePageAsyncEnumerator>();
                foreach ((IReadOnlyDictionary<PartitionKeyRange, OrderByContinuationToken> tokenMapping, string filter) in tokenMappingAndFilters)
                {
                    SqlQuerySpec rewrittenQueryForOrderBy = new SqlQuerySpec(
                        sqlQuerySpec.QueryText.Replace(oldValue: FormatPlaceHolder, newValue: filter),
                        sqlQuerySpec.Parameters);

                    foreach (KeyValuePair<PartitionKeyRange, OrderByContinuationToken> kvp in tokenMapping)
                    {
                        PartitionKeyRange range = kvp.Key;
                        OrderByContinuationToken token = kvp.Value;
                        OrderByQueryPartitionRangePageAsyncEnumerator remoteEnumerator = new OrderByQueryPartitionRangePageAsyncEnumerator(
                            documentContainer,
                            sqlQuerySpec,
                            range,
                            pageSize,
                            state: token != null ? new QueryState(CosmosString.Create(token.CompositeContinuationToken.Token)) : null);

                        remoteEnumerators.Add(remoteEnumerator);
                    }
                }
            }
        }

        private static TryCatch<PartitionMapping<OrderByContinuationToken>> MonadicGetOrderByContinuationTokenMapping(
            IReadOnlyList<PartitionKeyRange> partitionKeyRanges,
            CosmosElement continuationToken,
            int numOrderByItems)
        {
            if (partitionKeyRanges == null)
            {
                throw new ArgumentOutOfRangeException(nameof(partitionKeyRanges));
            }

            if (numOrderByItems < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(numOrderByItems));
            }

            if (continuationToken == null)
            {
                throw new ArgumentNullException(nameof(continuationToken));
            }

            TryCatch<List<OrderByContinuationToken>> monadicExtractContinuationTokens = MonadicExtractOrderByTokens(continuationToken, numOrderByItems);
            if (monadicExtractContinuationTokens.Failed)
            {
                return TryCatch<PartitionMapping<OrderByContinuationToken>>.FromException(monadicExtractContinuationTokens.Exception);
            }

            return MonadicGetPartitionMapping(
                partitionKeyRanges,
                monadicExtractContinuationTokens.Result);
        }

        private static TryCatch<List<OrderByContinuationToken>> MonadicExtractOrderByTokens(
            CosmosElement continuationToken,
            int numOrderByColumns)
        {
            if (continuationToken == null)
            {
                return TryCatch<List<OrderByContinuationToken>>.FromResult(default);
            }

            if (!(continuationToken is CosmosArray cosmosArray))
            {
                return TryCatch<List<OrderByContinuationToken>>.FromException(
                    new MalformedContinuationTokenException(
                        $"Order by continuation token must be an array: {continuationToken}."));
            }

            if (cosmosArray.Count == 0)
            {
                return TryCatch<List<OrderByContinuationToken>>.FromException(
                    new MalformedContinuationTokenException(
                        $"Order by continuation token cannot be empty: {continuationToken}."));
            }

            List<OrderByContinuationToken> orderByContinuationTokens = new List<OrderByContinuationToken>();
            foreach (CosmosElement arrayItem in cosmosArray)
            {
                TryCatch<OrderByContinuationToken> tryCreateOrderByContinuationToken = OrderByContinuationToken.TryCreateFromCosmosElement(arrayItem);
                if (!tryCreateOrderByContinuationToken.Succeeded)
                {
                    return TryCatch<List<OrderByContinuationToken>>.FromException(tryCreateOrderByContinuationToken.Exception);
                }

                orderByContinuationTokens.Add(tryCreateOrderByContinuationToken.Result);
            }

            foreach (OrderByContinuationToken suppliedOrderByContinuationToken in orderByContinuationTokens)
            {
                if (suppliedOrderByContinuationToken.OrderByItems.Count != numOrderByColumns)
                {
                    return TryCatch<List<OrderByContinuationToken>>.FromException(
                        new MalformedContinuationTokenException(
                            $"Invalid order-by items in continuation token {continuationToken} for OrderBy~Context."));
                }
            }

            return TryCatch<List<OrderByContinuationToken>>.FromResult(orderByContinuationTokens);
        }

        private static void AppendToBuilders((StringBuilder leftFilter, StringBuilder targetFilter, StringBuilder rightFilter) builders, object str)
        {
            OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, str, str, str);
        }

        private static void AppendToBuilders((StringBuilder leftFilter, StringBuilder targetFilter, StringBuilder rightFilter) builders, object left, object target, object right)
        {
            builders.leftFilter.Append(left);
            builders.targetFilter.Append(target);
            builders.rightFilter.Append(right);
        }

        private static (string leftFilter, string targetFilter, string rightFilter) GetFormattedFilters(
            ReadOnlyMemory<(OrderByColumn orderByColumn, CosmosElement orderByItem)> columnAndItems)
        {
            // When we run cross partition queries, 
            // we only serialize the continuation token for the partition that we left off on.
            // The only problem is that when we resume the order by query, 
            // we don't have continuation tokens for all other partition.
            // The saving grace is that the data has a composite sort order(query sort order, partition key range id)
            // so we can generate range filters which in turn the backend will turn into rid based continuation tokens,
            // which is enough to get the streams of data flowing from all partitions.
            // The details of how this is done is described below:
            int numOrderByItems = columnAndItems.Length;
            bool isSingleOrderBy = numOrderByItems == 1;
            StringBuilder left = new StringBuilder();
            StringBuilder target = new StringBuilder();
            StringBuilder right = new StringBuilder();

            (StringBuilder, StringBuilder, StringBuilder) builders = (left, target, right);

            if (isSingleOrderBy)
            {
                //For a single order by query we resume the continuations in this manner
                //    Suppose the query is SELECT* FROM c ORDER BY c.string ASC
                //        And we left off on partition N with the value "B"
                //        Then
                //            All the partitions to the left will have finished reading "B"
                //            Partition N is still reading "B"
                //            All the partitions to the right have let to read a "B
                //        Therefore the filters should be
                //            > "B" , >= "B", and >= "B" respectively
                //    Repeat the same logic for DESC and you will get
                //            < "B", <= "B", and <= "B" respectively
                //    The general rule becomes
                //        For ASC
                //            > for partitions to the left
                //            >= for the partition we left off on
                //            >= for the partitions to the right
                //        For DESC
                //            < for partitions to the left
                //            <= for the partition we left off on
                //            <= for the partitions to the right
                (OrderByColumn orderByColumn, CosmosElement orderByItem) = columnAndItems.Span[0];
                (string expression, SortOrder sortOrder) = (orderByColumn.Expression, orderByColumn.SortOrder);

                StringBuilder sb = new StringBuilder();
                CosmosElementToQueryLiteral cosmosElementToQueryLiteral = new CosmosElementToQueryLiteral(sb);
                orderByItem.Accept(cosmosElementToQueryLiteral);

                string orderByItemToString = sb.ToString();

                left.Append($"{expression} {(sortOrder == SortOrder.Descending ? Expressions.LessThan : Expressions.GreaterThan)} {orderByItemToString}");
                target.Append($"{expression} {(sortOrder == SortOrder.Descending ? Expressions.LessThanOrEqualTo : Expressions.GreaterThanOrEqualTo)} {orderByItemToString}");
                right.Append($"{expression} {(sortOrder == SortOrder.Descending ? Expressions.LessThanOrEqualTo : Expressions.GreaterThanOrEqualTo)} {orderByItemToString}");
            }
            else
            {
                //For a multi order by query
                //    Suppose the query is SELECT* FROM c ORDER BY c.string ASC, c.number ASC
                //        And we left off on partition N with the value("A", 1)
                //        Then
                //            All the partitions to the left will have finished reading("A", 1)
                //            Partition N is still reading("A", 1)
                //            All the partitions to the right have let to read a "(A", 1)
                //        The filters are harder to derive since their are multiple columns
                //        But the problem reduces to "How do you know one document comes after another in a multi order by query"
                //        The answer is to just look at it one column at a time.
                //        For this particular scenario:
                //        If a first column is greater ex. ("B", blah), then the document comes later in the sort order
                //            Therefore we want all documents where the first column is greater than "A" which means > "A"
                //        Or if the first column is a tie, then you look at the second column ex. ("A", blah).
                //            Therefore we also want all documents where the first column was a tie but the second column is greater which means = "A" AND > 1
                //        Therefore the filters should be
                //            (> "A") OR (= "A" AND > 1), (> "A") OR (= "A" AND >= 1), (> "A") OR (= "A" AND >= 1)
                //            Notice that if we repeated the same logic we for single order by we would have gotten
                //            > "A" AND > 1, >= "A" AND >= 1, >= "A" AND >= 1
                //            which is wrong since we missed some documents
                //    Repeat the same logic for ASC, DESC
                //            (> "A") OR (= "A" AND < 1), (> "A") OR (= "A" AND <= 1), (> "A") OR (= "A" AND <= 1)
                //        Again for DESC, ASC
                //            (< "A") OR (= "A" AND > 1), (< "A") OR (= "A" AND >= 1), (< "A") OR (= "A" AND >= 1)
                //        And again for DESC DESC
                //            (< "A") OR (= "A" AND < 1), (< "A") OR (= "A" AND <= 1), (< "A") OR (= "A" AND <= 1)
                //    The general we look at all prefixes of the order by columns to look for tie breakers.
                //        Except for the full prefix whose last column follows the rules for single item order by
                //        And then you just OR all the possibilities together
                for (int prefixLength = 1; prefixLength <= numOrderByItems; prefixLength++)
                {
                    ReadOnlySpan<(OrderByColumn orderByColumn, CosmosElement orderByItem)> columnAndItemPrefix = columnAndItems.Span.Slice(start: 0, length: prefixLength);

                    bool lastPrefix = prefixLength == numOrderByItems;
                    bool firstPrefix = prefixLength == 1;

                    OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, "(");

                    for (int index = 0; index < prefixLength; index++)
                    {
                        string expression = columnAndItemPrefix[index].orderByColumn.Expression;
                        SortOrder sortOrder = columnAndItemPrefix[index].orderByColumn.SortOrder;
                        CosmosElement orderByItem = columnAndItemPrefix[index].orderByItem;
                        bool lastItem = index == prefixLength - 1;

                        // Append Expression
                        OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, expression);
                        OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, " ");

                        // Append binary operator
                        if (lastItem)
                        {
                            string inequality = sortOrder == SortOrder.Descending ? Expressions.LessThan : Expressions.GreaterThan;
                            OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, inequality);
                            if (lastPrefix)
                            {
                                OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, string.Empty, Expressions.EqualTo, Expressions.EqualTo);
                            }
                        }
                        else
                        {
                            OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, Expressions.EqualTo);
                        }

                        // Append SortOrder
                        StringBuilder sb = new StringBuilder();
                        CosmosElementToQueryLiteral cosmosElementToQueryLiteral = new CosmosElementToQueryLiteral(sb);
                        orderByItem.Accept(cosmosElementToQueryLiteral);
                        string orderByItemToString = sb.ToString();
                        OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, " ");
                        OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, orderByItemToString);
                        OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, " ");

                        if (!lastItem)
                        {
                            OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, "AND ");
                        }
                    }

                    OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, ")");
                    if (!lastPrefix)
                    {
                        OrderByCrossPartitionQueryPipelineStage.AppendToBuilders(builders, " OR ");
                    }
                }
            }

            // For the target filter we can make an optimization to just return "true",
            // since we already have the backend continuation token to resume with.
            return (left.ToString(), TrueFilter, right.ToString());
        }

        /// <summary>
        /// When resuming an order by query we need to filter the document producers.
        /// </summary>
        /// <param name="enumerator">The producer to filter down.</param>
        /// <param name="sortOrders">The sort orders.</param>
        /// <param name="continuationToken">The continuation token.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task to await on.</returns>
        private static async Task<TryCatch> MonadicFilterAsync(
            OrderByQueryPartitionRangePageAsyncEnumerator enumerator,
            IReadOnlyList<SortOrder> sortOrders,
            OrderByContinuationToken continuationToken,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // When we resume a query on a partition there is a possibility that we only read a partial page from the backend
            // meaning that will we repeat some documents if we didn't do anything about it. 
            // The solution is to filter all the documents that come before in the sort order, since we have already emitted them to the client.
            // The key is to seek until we get an order by value that matches the order by value we left off on.
            // Once we do that we need to seek to the correct _rid within the term,
            // since there might be many documents with the same order by value we left off on.

            if (!ResourceId.TryParse(continuationToken.Rid, out ResourceId continuationRid))
            {
                return TryCatch.FromException(
                    new MalformedContinuationTokenException(
                        $"Invalid Rid in the continuation token {continuationToken.CompositeContinuationToken.Token} for OrderBy~Context."));
            }

            Dictionary<string, ResourceId> resourceIds = new Dictionary<string, ResourceId>();
            int itemToSkip = continuationToken.SkipCount;
            bool continuationRidVerified = false;

            // Throw away documents until it matches the item from the continuation token.
            while (await enumerator.MoveNextAsync())
            {
                TryCatch<OrderByQueryPage> monadicOrderByQueryPage = enumerator.Current;
                if (monadicOrderByQueryPage.Failed)
                {
                    return TryCatch.FromException(monadicOrderByQueryPage.Exception);
                }

                OrderByQueryPage orderByQueryPage = monadicOrderByQueryPage.Result;
                IEnumerator<CosmosElement> documents = orderByQueryPage.Enumerator;
                while (documents.MoveNext())
                {
                    OrderByQueryResult orderByResult = new OrderByQueryResult(documents.Current);

                    int cmp = 0;
                    for (int i = 0; (i < sortOrders.Count) && (cmp == 0); ++i)
                    {
                        cmp = ItemComparer.Instance.Compare(
                            continuationToken.OrderByItems[i].Item,
                            orderByResult.OrderByItems[i].Item);

                        if (cmp != 0)
                        {
                            cmp = sortOrders[i] == SortOrder.Ascending ? cmp : -cmp;
                        }
                    }

                    if (cmp < 0)
                    {
                        // We might have passed the item due to deletions and filters.
                        return TryCatch.FromResult();
                    }

                    if (cmp == 0)
                    {
                        if (!resourceIds.TryGetValue(orderByResult.Rid, out ResourceId rid))
                        {
                            if (!ResourceId.TryParse(orderByResult.Rid, out rid))
                            {
                                return TryCatch.FromException(
                                    new MalformedContinuationTokenException(
                                        $"Invalid Rid in the continuation token {continuationToken.CompositeContinuationToken.Token} for OrderBy~Context~TryParse."));
                            }

                            resourceIds.Add(orderByResult.Rid, rid);
                        }

                        if (!continuationRidVerified)
                        {
                            if (continuationRid.Database != rid.Database || continuationRid.DocumentCollection != rid.DocumentCollection)
                            {
                                return TryCatch.FromException(
                                    new MalformedContinuationTokenException(
                                        $"Invalid Rid in the continuation token {continuationToken.CompositeContinuationToken.Token} for OrderBy~Context."));
                            }

                            continuationRidVerified = true;
                        }

                        // Once the item matches the order by items from the continuation tokens
                        // We still need to remove all the documents that have a lower rid in the rid sort order.
                        // If there is a tie in the sort order the documents should be in _rid order in the same direction as the index (given by the backend)
                        cmp = continuationRid.Document.CompareTo(rid.Document);
                        if ((orderByQueryPage.Page.CosmosQueryExecutionInfo == null) || orderByQueryPage.Page.CosmosQueryExecutionInfo.ReverseRidEnabled)
                        {
                            // If reverse rid is enabled on the backend then fallback to the old way of doing it.
                            if (sortOrders[0] == SortOrder.Descending)
                            {
                                cmp = -cmp;
                            }
                        }
                        else
                        {
                            // Go by the whatever order the index wants
                            if (orderByQueryPage.Page.CosmosQueryExecutionInfo.ReverseIndexScan)
                            {
                                cmp = -cmp;
                            }
                        }

                        // We might have passed the item due to deletions and filters.
                        // We also have a skip count for JOINs
                        if (cmp < 0 || (cmp == 0 && itemToSkip-- <= 0))
                        {
                            return TryCatch.FromResult();
                        }
                    }
                }
            }

            return TryCatch.FromResult();
        }

        private static bool IsSplitException(Exception exeception)
        {
            return exeception is CosmosException cosmosException
                && (cosmosException.StatusCode == HttpStatusCode.Gone)
                && (cosmosException.SubStatusCode == (int)Documents.SubStatusCodes.PartitionKeyRangeGone);
        }

        public readonly struct OrderByColumn
        {
            public OrderByColumn(string expression, SortOrder sortOrder)
            {
                this.Expression = expression ?? throw new ArgumentNullException(nameof(expression));
                this.SortOrder = sortOrder;
            }

            public string Expression { get; }
            public SortOrder SortOrder { get; }
        }
    }
}
