using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.SmartContract.Infrastructure;
using AElf.Kernel.SmartContract.Sdk;
using AElf.Kernel.SmartContractExecution.Events;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Local;

namespace AElf.Kernel.SmartContract.Application
{
    public class LocalTransactionExecutingService : ILocalTransactionExecutingService, ISingletonDependency
    {
        private readonly ISmartContractExecutiveService _smartContractExecutiveService;
        private readonly List<IPreExecutionPlugin> _prePlugins;
        private readonly List<IPostExecutionPlugin> _postPlugins;
        private readonly ITransactionResultService _transactionResultService;
        private readonly ContractOptions _contractOptions;
        public ILogger<LocalTransactionExecutingService> Logger { get; set; }

        public ILocalEventBus LocalEventBus { get; set; }

        public LocalTransactionExecutingService(ITransactionResultService transactionResultService,
            ISmartContractExecutiveService smartContractExecutiveService,
            IEnumerable<IPostExecutionPlugin> postPlugins, IEnumerable<IPreExecutionPlugin> prePlugins,
            IOptionsSnapshot<ContractOptions> contractOptionsSnapshot
        )
        {
            _transactionResultService = transactionResultService;
            _smartContractExecutiveService = smartContractExecutiveService;
            _prePlugins = GetUniquePrePlugins(prePlugins);
            _postPlugins = GetUniquePostPlugins(postPlugins);
            _contractOptions = contractOptionsSnapshot.Value;
            Logger = NullLogger<LocalTransactionExecutingService>.Instance;
            LocalEventBus = NullLocalEventBus.Instance;
        }

        public async Task<List<ExecutionReturnSet>> ExecuteAsync(TransactionExecutingDto transactionExecutingDto,
            CancellationToken cancellationToken, bool throwException)
        {
            try
            {
                var groupStateCache = transactionExecutingDto.PartialBlockStateSet == null
                    ? new TieredStateCache()
                    : new TieredStateCache(
                        new StateCacheFromPartialBlockStateSet(transactionExecutingDto.PartialBlockStateSet));
                var groupChainContext = new ChainContextWithTieredStateCache(
                    transactionExecutingDto.BlockHeader.PreviousBlockHash,
                    transactionExecutingDto.BlockHeader.Height - 1, groupStateCache);

                var transactionResults = new List<TransactionResult>();
                var returnSets = new List<ExecutionReturnSet>();
                foreach (var transaction in transactionExecutingDto.Transactions)
                {
                    TransactionTrace trace;
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var singleTxExecutingDto = new SingleTransactionExecutingDto
                    {
                        Depth = 0,
                        ChainContext = groupChainContext,
                        Transaction = transaction,
                        CurrentBlockTime = transactionExecutingDto.BlockHeader.Time,
                    };
                    try
                    {
                        var transactionExecutionTask = Task.Run(() => ExecuteOneAsync(singleTxExecutingDto,
                            cancellationToken), cancellationToken);
                        
                        trace = await transactionExecutionTask.WithCancellation(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.LogTrace("Transaction canceled.");
                        if (cancellationToken.IsCancellationRequested)
                            break;
                        continue;
                    }
                    
                    if (!TryUpdateGroupStateCache(groupStateCache, trace, throwException))
                        break;
                    var result = GetTransactionResult(trace, transactionExecutingDto.BlockHeader.Height);

                    if (result != null)
                    {
                        result.TransactionFee = trace.TransactionFee;
                        result.ConsumedResourceTokens = trace.ConsumedResourceTokens;
                        transactionResults.Add(result);
                    }

                    var returnSet = GetReturnSet(trace, result);
                    returnSets.Add(returnSet);
                }

                await _transactionResultService.AddTransactionResultsAsync(transactionResults,
                    transactionExecutingDto.BlockHeader);
                return returnSets;
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Failed while executing txs in block.");
                throw;
            }
        }

        private bool TryUpdateGroupStateCache(TieredStateCache groupStateCache, TransactionTrace trace,
            bool throwException)
        {
            if (trace == null) return false;

            if (!trace.IsSuccessful())
            {
                if (throwException)
                {
                    Logger.LogError(trace.Error);
                }

                // Do not package this transaction if any of his inline transactions canceled.
                if (IsTransactionCanceled(trace))
                {
                    return false;
                }

                var transactionExecutingStateSets = new List<TransactionExecutingStateSet>();
                foreach (var preTrace in trace.PreTraces)
                {
                    if (preTrace.IsSuccessful()) transactionExecutingStateSets.AddRange(preTrace.GetStateSets());
                }

                foreach (var postTrace in trace.PostTraces)
                {
                    if (postTrace.IsSuccessful()) transactionExecutingStateSets.AddRange(postTrace.GetStateSets());
                }

                groupStateCache.Update(transactionExecutingStateSets);
                trace.SurfaceUpError();
            }
            else
            {
                groupStateCache.Update(trace.GetStateSets());
            }

            if (trace.Error != string.Empty)
            {
                Logger.LogError(trace.Error);
            }

            return true;
        }

        private static bool IsTransactionCanceled(TransactionTrace trace)
        {
            return trace.ExecutionStatus == ExecutionStatus.Canceled || trace.PreTraces.Any(IsTransactionCanceled) ||
                   trace.InlineTraces.Any(IsTransactionCanceled) || trace.PostTraces.Any(IsTransactionCanceled);
        }

        private async Task<TransactionTrace> ExecuteOneAsync(SingleTransactionExecutingDto singleTxExecutingDto,
            CancellationToken cancellationToken)
        {
            if (singleTxExecutingDto.IsCancellable)
                cancellationToken.ThrowIfCancellationRequested();

            var txContext = CreateTransactionContext(singleTxExecutingDto);

            var internalStateCache = new TieredStateCache(singleTxExecutingDto.ChainContext.StateCache);
            var internalChainContext =
                new ChainContextWithTieredStateCache(singleTxExecutingDto.ChainContext, internalStateCache);

            IExecutive executive;
            try
            {
                executive = await _smartContractExecutiveService.GetExecutiveAsync(
                    internalChainContext,
                    singleTxExecutingDto.Transaction.To);
            }
            catch (SmartContractFindRegistrationException)
            {
                txContext.Trace.ExecutionStatus = ExecutionStatus.ContractError;
                txContext.Trace.Error += "Invalid contract address.\n";
                return txContext.Trace;
            }

            var exePluginTxDto = new ExecutePluginTransactionDto
            {
                Executive = executive,
                TxContext = txContext,
                CurrentBlockTime = singleTxExecutingDto.CurrentBlockTime,
                InternalChainContext = internalChainContext,
                InternalStateCache = internalStateCache
            };

            var trace = await GetTransactionTraceAsync(singleTxExecutingDto, exePluginTxDto, cancellationToken);
            return trace;
        }

        private async Task<TransactionTrace> GetTransactionTraceAsync(
            SingleTransactionExecutingDto singleTxExecutingDto,
            ExecutePluginTransactionDto exePluginTxDto,
            CancellationToken cancellationToken)
        {
            var trace = exePluginTxDto.TxContext.Trace;
            try
            {
                #region PreTransaction

                if (singleTxExecutingDto.Depth == 0)
                {
                    if (!await ExecutePluginOnPreTransactionStageAsync(exePluginTxDto, cancellationToken))
                    {
                        trace.ExecutionStatus = ExecutionStatus.Prefailed;
                        return trace;
                    }
                }

                #endregion

                await exePluginTxDto.Executive.ApplyAsync(exePluginTxDto.TxContext);

                if (trace.IsSuccessful())
                    await ExecuteInlineTransactions(singleTxExecutingDto.Depth, singleTxExecutingDto.CurrentBlockTime,
                        exePluginTxDto.TxContext, exePluginTxDto.InternalStateCache,
                        exePluginTxDto.InternalChainContext, cancellationToken);

                #region PostTransaction

                if (singleTxExecutingDto.Depth == 0)
                {
                    if (!await ExecutePluginOnPostTransactionStageAsync(exePluginTxDto, cancellationToken))
                    {
                        trace.ExecutionStatus = ExecutionStatus.Postfailed;
                        return trace;
                    }
                }

                return trace;

                #endregion
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Transaction execution failed.");
                trace.ExecutionStatus = ExecutionStatus.ContractError;
                trace.Error += ex + "\n";
                throw;
            }
            finally
            {
                await _smartContractExecutiveService.PutExecutiveAsync(singleTxExecutingDto.Transaction.To,
                    exePluginTxDto.Executive);
#if DEBUG
                await LocalEventBus.PublishAsync(new TransactionExecutedEventData
                {
                    TransactionTrace = trace
                });
#endif
            }
        }

        private async Task ExecuteInlineTransactions(int depth, Timestamp currentBlockTime,
            ITransactionContext txContext, TieredStateCache internalStateCache,
            IChainContext internalChainContext, CancellationToken cancellationToken)
        {
            var trace = txContext.Trace;
            internalStateCache.Update(txContext.Trace.GetStateSets());
            foreach (var inlineTx in txContext.Trace.InlineTransactions)
            {
                var singleTxExecutingDto = new SingleTransactionExecutingDto
                {
                    Depth = depth + 1,
                    ChainContext = internalChainContext,
                    Transaction = inlineTx,
                    CurrentBlockTime = currentBlockTime,
                    Origin = txContext.Origin
                };
                var inlineTrace = await ExecuteOneAsync(singleTxExecutingDto, cancellationToken);
                
                if (inlineTrace == null)
                    break;
                trace.InlineTraces.Add(inlineTrace);
                if (!inlineTrace.IsSuccessful())
                {
                    Logger.LogWarning($"Method name: {inlineTx.MethodName}, {inlineTrace.Error}");
                    // Already failed, no need to execute remaining inline transactions
                    break;
                }

                internalStateCache.Update(inlineTrace.GetStateSets());
            }
        }

        private async Task<bool> ExecutePluginOnPreTransactionStageAsync(ExecutePluginTransactionDto exePluginTxDto,
            CancellationToken cancellationToken)
        {
            var trace = exePluginTxDto.TxContext.Trace;
            foreach (var plugin in _prePlugins)
            {
                var transactions = await plugin.GetPreTransactionsAsync(exePluginTxDto.Executive.Descriptors, exePluginTxDto.TxContext);
                foreach (var preTx in transactions)
                {
                    var singleTxExecutingDto = new SingleTransactionExecutingDto
                    {
                        Depth = 0,
                        ChainContext = exePluginTxDto.InternalChainContext,
                        Transaction = preTx,
                        CurrentBlockTime = exePluginTxDto.CurrentBlockTime
                    };
                    var preTrace = await ExecuteOneAsync(singleTxExecutingDto, cancellationToken);
                    if (preTrace == null)
                        return false;
                    trace.PreTransactions.Add(preTx);
                    trace.PreTraces.Add(preTrace);
                    if (preTx.MethodName == "ChargeTransactionFees")
                    {
                        var txFee = new TransactionFee();
                        txFee.MergeFrom(preTrace.ReturnValue);
                        trace.TransactionFee = txFee;
                    }
                    
                    if (!preTrace.IsSuccessful())
                    {
                        return false;
                    }

                    var stateSets = preTrace.GetStateSets().ToList();
                    exePluginTxDto.InternalStateCache.Update(stateSets);
                    var parentStateCache = exePluginTxDto.TxContext.StateCache as TieredStateCache;
                    parentStateCache?.Update(stateSets);

                    if (trace.TransactionFee == null || !trace.TransactionFee.IsFailedToCharge) continue;

                    preTrace.ExecutionStatus = ExecutionStatus.Executed;
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> ExecutePluginOnPostTransactionStageAsync(ExecutePluginTransactionDto exePluginTxDto,
            CancellationToken cancellationToken)
        {
            var trace = exePluginTxDto.TxContext.Trace;
            if (!trace.IsSuccessful())
            {
                exePluginTxDto.InternalStateCache = new TieredStateCache(exePluginTxDto.TxContext.StateCache);
                foreach (var preTrace in exePluginTxDto.TxContext.Trace.PreTraces)
                {
                    var stateSets = preTrace.GetStateSets();
                    exePluginTxDto.InternalStateCache.Update(stateSets);
                }

                exePluginTxDto.InternalChainContext.StateCache = exePluginTxDto.InternalStateCache;
            }
            
            foreach (var plugin in _postPlugins)
            {
                var transactions = await plugin.GetPostTransactionsAsync(exePluginTxDto.Executive.Descriptors, exePluginTxDto.TxContext);
                foreach (var postTx in transactions)
                {
                    var singleTxExecutingDto = new SingleTransactionExecutingDto
                    {
                        Depth = 0,
                        ChainContext = exePluginTxDto.InternalChainContext,
                        Transaction = postTx,
                        CurrentBlockTime = exePluginTxDto.CurrentBlockTime
                    };
                    var postTrace = await ExecuteOneAsync(singleTxExecutingDto, cancellationToken);
                    
                    if (postTrace == null)
                        return false;
                    trace.PostTransactions.Add(postTx);
                    trace.PostTraces.Add(postTrace);

                    if (postTx.MethodName == "ChargeResourceToken")
                    {
                        var consumedResourceTokens = new ConsumedResourceTokens();
                        consumedResourceTokens.MergeFrom(postTrace.ReturnValue);
                        trace.ConsumedResourceTokens = consumedResourceTokens;
                    }

                    if (!postTrace.IsSuccessful())
                    {
                        return false;
                    }

                    exePluginTxDto.InternalStateCache.Update(postTrace.GetStateSets());
                }
            }

            return true;
        }

        private TransactionResult GetTransactionResult(TransactionTrace trace, long blockHeight)
        {
            ITransactionResultFactory factory;
            if (trace.ExecutionStatus == ExecutionStatus.Undefined)
            {
                factory = new UnExecutableTxResultFactory();
            }

            else if (trace.ExecutionStatus == ExecutionStatus.Prefailed)
            {
                factory = new PreFailedTxResultFactory();
            }

            else if (trace.IsSuccessful())
            {
                factory = new MinedTxResultFactory();
            }

            else
            {
                factory = new FailedTxResultFactory();
            }

            return factory.GetTransactionResult(trace, blockHeight);
        }

        private ExecutionReturnSet GetReturnSet(TransactionTrace trace, TransactionResult result)
        {
            var returnSet = new ExecutionReturnSet
            {
                TransactionId = result.TransactionId,
                Status = result.Status,
                Bloom = result.Bloom
            };

            if (trace.IsSuccessful())
            {
                var transactionExecutingStateSets = trace.GetStateSets();
                returnSet = GetReturnSet(returnSet, transactionExecutingStateSets);
                returnSet.ReturnValue = trace.ReturnValue;
            }
            else
            {
                var transactionExecutingStateSets = new List<TransactionExecutingStateSet>();
                foreach (var preTrace in trace.PreTraces)
                {
                    if (preTrace.IsSuccessful()) transactionExecutingStateSets.AddRange(preTrace.GetStateSets());
                }
                    
                foreach (var postTrace in trace.PostTraces)
                {
                    if (postTrace.IsSuccessful()) transactionExecutingStateSets.AddRange(postTrace.GetStateSets());
                }

                returnSet = GetReturnSet(returnSet, transactionExecutingStateSets);
            }

            var reads = trace.GetFlattenedReads();
            foreach (var read in reads)
            {
                returnSet.StateAccesses[read.Key] = read.Value;
            }

            return returnSet;
        }
        
        private ExecutionReturnSet GetReturnSet(ExecutionReturnSet returnSet,
            IEnumerable<TransactionExecutingStateSet> transactionExecutingStateSets)
        {
            foreach (var transactionExecutingStateSet in transactionExecutingStateSets)
            {
                foreach (var write in transactionExecutingStateSet.Writes)
                {
                    returnSet.StateChanges[write.Key] = write.Value;
                    returnSet.StateDeletes.Remove(write.Key);
                }
                
                foreach (var delete in transactionExecutingStateSet.Deletes)
                {
                    returnSet.StateDeletes[delete.Key] = delete.Value;
                    returnSet.StateChanges.Remove(delete.Key);
                }
            }

            return returnSet;
        }

        private static List<IPreExecutionPlugin> GetUniquePrePlugins(IEnumerable<IPreExecutionPlugin> plugins)
        {
            // One instance per type
            return plugins.ToLookup(p => p.GetType()).Select(coll => coll.First()).ToList();
        }

        private static List<IPostExecutionPlugin> GetUniquePostPlugins(IEnumerable<IPostExecutionPlugin> plugins)
        {
            // One instance per type
            return plugins.ToLookup(p => p.GetType()).Select(coll => coll.First()).ToList();
        }

        private TransactionContext CreateTransactionContext(SingleTransactionExecutingDto singleTxExecutingDto)
        {
            if (singleTxExecutingDto.Transaction.To == null || singleTxExecutingDto.Transaction.From == null)
            {
                throw new Exception($"error tx: {singleTxExecutingDto.Transaction}");
            }

            var trace = new TransactionTrace
            {
                TransactionId = singleTxExecutingDto.Transaction.GetHash()
            };
            var txContext = new TransactionContext
            {
                PreviousBlockHash = singleTxExecutingDto.ChainContext.BlockHash,
                CurrentBlockTime = singleTxExecutingDto.CurrentBlockTime,
                Transaction = singleTxExecutingDto.Transaction,
                BlockHeight = singleTxExecutingDto.ChainContext.BlockHeight + 1,
                Trace = trace,
                CallDepth = singleTxExecutingDto.Depth,
                StateCache = singleTxExecutingDto.ChainContext.StateCache,
                Origin = singleTxExecutingDto.Origin != null
                    ? singleTxExecutingDto.Origin
                    : singleTxExecutingDto.Transaction.From
            };

            return txContext;
        }
    }
}