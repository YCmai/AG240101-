using AciModule.Domain.Entitys;
using AciModule.Domain.Shared;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Local;
using Volo.Abp.Uow;

namespace AciModule.Domain.Service
{
    public class AciAppManager : ISingletonDependency, IDisposable
    {
        private const int DefaultLocalPort = 30001;
        private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HandshakeRetryInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan HandshakeObserveInterval = TimeSpan.FromMilliseconds(500);
        private const int InitialGlobalParamIndex = 0;
        private const int InitialGlobalParamNumber = 1;
        private const int InitialGlobalParamValue = 2;

        private readonly AciConnection _aciClient;
        private readonly ConcurrentQueue<AciEvent> _aciEventQueue;
        private readonly ILocalEventBus _eventBus;
        private readonly ILogger<AciAppManager> _logger;
        private readonly CancellationTokenSource _reconnectCts = new();
        private readonly Task _reconnectTask;
        private CancellationTokenSource _handshakeCts;
        private Task _handshakeTask;
        private int _handshakeStarted;
        private int _handshakeCompleted;

        public int AciEventCount { get; set; } = 0;
        public AciConnection AciClient => _aciClient;
        public ConcurrentQueue<AciEvent> AciEventQueue => _aciEventQueue;

        public AciAppManager(ILocalEventBus eventBus, ILogger<AciAppManager> logger)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _aciEventQueue = new ConcurrentQueue<AciEvent>();
            _aciClient = new AciConnection();

            _aciClient.RequestDataReceived += AciClient_RequestDataReceived;
            _aciClient.ConnectedChanged += AciClient_ConnectedChanged;

            ResetLocalEndpoint();
            _reconnectTask = Task.Run(() => RunReconnectLoopAsync(_reconnectCts.Token));
        }

        public void Dispose()
        {
            _reconnectCts.Cancel();
            CancelHandshake();
            _aciClient.RequestDataReceived -= AciClient_RequestDataReceived;
            _aciClient.ConnectedChanged -= AciClient_ConnectedChanged;
            _aciClient.Dispose();
            _reconnectCts.Dispose();
            _handshakeCts?.Dispose();
        }

        [UnitOfWork]
        public virtual async void AciClient_RequestDataReceived(object sender, AciDataEventArgs e)
        {
            _logger.LogCritical("收到ACI事件，类型: {DataType}", e.DataType);
            await _eventBus.PublishAsync(eventData: e);
        }

        private void AciClient_ConnectedChanged(object sender, EventArgs e)
        {
            if (_aciClient.Connected)
            {
                _logger.LogCritical("ACI连接已建立，开始执行握手流程。");
                StartHandshake();
                return;
            }

            CancelHandshake();
            Interlocked.Exchange(ref _handshakeStarted, 0);
            Interlocked.Exchange(ref _handshakeCompleted, 0);
            _logger.LogError("ACI连接已断开，等待后台主动重连。");
        }

        private void InitialHostCallBack(AciCommandData data)
        {
            // 握手已迁移为独立异步流程，这里保留兼容旧调用。
        }

        private async Task RunReconnectLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(ReconnectInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    if (_aciClient.Connected)
                    {
                        continue;
                    }

                    try
                    {
                        ResetLocalEndpoint();
                        _logger.LogCritical("检测到ACI未连接，已重新绑定本地监听端口 {Port}。", DefaultLocalPort);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "执行ACI主动重连时发生异常。");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void ResetLocalEndpoint()
        {
            _aciClient.SetServerLocalIP(DefaultLocalPort);
        }

        private void StartHandshake()
        {
            if (Interlocked.Exchange(ref _handshakeStarted, 1) == 1)
            {
                return;
            }

            CancelHandshake();
            _handshakeCts = new CancellationTokenSource();
            _handshakeTask = Task.Run(() => ExecuteHandshakeAsync(_handshakeCts.Token));
        }

        public void RestartHandshake(string reason)
        {
            _logger.LogCritical("ACI触发强制重握手，原因: {Reason}。", reason);
            Interlocked.Exchange(ref _handshakeCompleted, 0);
            Interlocked.Exchange(ref _handshakeStarted, 0);
            CancelHandshake();

            if (_aciClient.Connected)
            {
                StartHandshake();
            }
            else
            {
                _logger.LogError("ACI强制重握手时连接未建立，等待后台重连后再次握手。原因: {Reason}。", reason);
            }
        }

        private void CancelHandshake()
        {
            if (_handshakeCts == null)
            {
                return;
            }

            try
            {
                _handshakeCts.Cancel();
            }
            catch
            {
            }
            finally
            {
                _handshakeCts.Dispose();
                _handshakeCts = null;
                _handshakeTask = null;
            }
        }

        private async Task ExecuteHandshakeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _aciClient.Connected)
                {
                    var readResult = await SendGlobalParamReadAsync(InitialGlobalParamIndex, InitialGlobalParamNumber, cancellationToken);
                    LogGlobalParamCommandResult("Read", readResult);

                    if (!TryGetHandshakeStatus(readResult, out var paramValue))
                    {
                        _logger.LogError("ACI握手读取全局参数失败，500ms后重试。");
                        await Task.Delay(HandshakeRetryInterval, cancellationToken);
                        continue;
                    }

                    _logger.LogCritical("ACI握手读取成功，当前参数值={ParamValue}。", paramValue);

                    var writeResult = await SendGlobalParamWriteAsync(
                        InitialGlobalParamIndex,
                        InitialGlobalParamNumber,
                        new[] { InitialGlobalParamValue },
                        cancellationToken);
                    LogGlobalParamCommandResult("Write", writeResult);

                    if (writeResult.ErrorCode != AciCommandErrorCode.None)
                    {
                        _logger.LogError("ACI握手写入参数2失败，ErrorCode={ErrorCode}。", writeResult.ErrorCode);
                        await Task.Delay(HandshakeRetryInterval, cancellationToken);
                        continue;
                    }

                    _logger.LogCritical("ACI握手已发送全局参数写入2。");

                    if (paramValue == InitialGlobalParamValue)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                        var finalWriteResult = await SendGlobalParamWriteAsync(
                            InitialGlobalParamIndex,
                            InitialGlobalParamNumber,
                            new[] { InitialGlobalParamValue },
                            cancellationToken);
                        LogGlobalParamCommandResult("WriteConfirm", finalWriteResult);

                        if (finalWriteResult.ErrorCode != AciCommandErrorCode.None)
                        {
                            _logger.LogError("ACI握手补发参数2失败，ErrorCode={ErrorCode}。", finalWriteResult.ErrorCode);
                            await Task.Delay(HandshakeRetryInterval, cancellationToken);
                            continue;
                        }

                        var verifyResult = await SendGlobalParamReadAsync(InitialGlobalParamIndex, InitialGlobalParamNumber, cancellationToken);
                        LogGlobalParamCommandResult("ReadVerify", verifyResult);

                        if (TryGetHandshakeStatus(verifyResult, out var verifiedValue) && verifiedValue == InitialGlobalParamValue)
                        {
                            Interlocked.Exchange(ref _handshakeCompleted, 1);
                            _logger.LogCritical("ACI握手完成，写后回读确认值={ParamValue}。", verifiedValue);
                            await ObservePostHandshakeReadsAsync(cancellationToken);
                            return;
                        }

                        _logger.LogError("ACI握手写后回读未确认到2，当前值={ParamValue}，500ms后重试。", verifiedValue);
                        await Task.Delay(HandshakeRetryInterval, cancellationToken);
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行ACI握手流程时发生未捕获异常。");
            }
            finally
            {
                Interlocked.Exchange(ref _handshakeStarted, 0);
            }
        }

        private bool TryGetHandshakeStatus(AciCommandData commandData, out int paramValue)
        {
            paramValue = 0;

            if (commandData == null || commandData.ErrorCode != AciCommandErrorCode.None)
            {
                return false;
            }

            if (commandData.AcknowledgeData is not GlobalParamStatusAciData ack || ack.ParamValues == null || ack.ParamValues.Length < 1)
            {
                return false;
            }

            paramValue = ack.ParamValues[0];
            return true;
        }

        private void LogGlobalParamCommandResult(string phase, AciCommandData commandData)
        {
            var request = commandData?.RequestData as GlobalParamCommandAciData;
            var ack = commandData?.AcknowledgeData as GlobalParamStatusAciData;
            int? ackParamValue = ack?.ParamValues != null && ack.ParamValues.Length > 0 ? ack.ParamValues[0] : null;

            _logger.LogCritical(
                "ACI握手 {Phase}: RequestMagicIndex={RequestMagicIndex}, AckMagicIndex={AckMagicIndex}, ErrorCode={ErrorCode}, Acknowledged={Acknowledged}, AckParamValue0={AckParamValue0}",
                phase,
                request?.MagicIndex,
                ack?.MagicIndex,
                commandData?.ErrorCode,
                commandData?.Acknowledged,
                ackParamValue);
        }

        private async Task ObservePostHandshakeReadsAsync(CancellationToken cancellationToken)
        {
            const int observeCount = 5;

            for (var i = 1; i <= observeCount; i++)
            {
                await Task.Delay(HandshakeObserveInterval, cancellationToken);

                var observeResult = await SendGlobalParamReadAsync(InitialGlobalParamIndex, InitialGlobalParamNumber, cancellationToken);
                LogGlobalParamCommandResult($"PostHandshakeRead[{i}]", observeResult);

                if (TryGetHandshakeStatus(observeResult, out var observeValue))
                {
                    _logger.LogCritical("ACI握手后连续回读第 {Index} 次，当前值={ParamValue}。", i, observeValue);

                    if (observeValue != InitialGlobalParamValue)
                    {
                        _logger.LogError("ACI握手后连续回读发现参数回落为 {ParamValue}，准备自动补写2。", observeValue);

                        var recovered = await TryRecoverHandshakeStateAsync(cancellationToken);
                        if (!recovered)
                        {
                            Interlocked.Exchange(ref _handshakeCompleted, 0);
                            _logger.LogError("ACI握手后自动补写2失败，结束本轮观察。");
                            return;
                        }
                    }
                }
                else
                {
                    _logger.LogError("ACI握手后连续回读第 {Index} 次失败。", i);
                }
            }
        }

        private async Task<bool> TryRecoverHandshakeStateAsync(CancellationToken cancellationToken)
        {
            const int maxRetryCount = 3;

            for (var retry = 1; retry <= maxRetryCount; retry++)
            {
                var writeResult = await SendGlobalParamWriteAsync(
                    InitialGlobalParamIndex,
                    InitialGlobalParamNumber,
                    new[] { InitialGlobalParamValue },
                    cancellationToken);
                LogGlobalParamCommandResult($"RecoverWrite[{retry}]", writeResult);

                if (writeResult.ErrorCode != AciCommandErrorCode.None)
                {
                    _logger.LogError("ACI自动补写2第 {Retry} 次失败，ErrorCode={ErrorCode}。", retry, writeResult.ErrorCode);
                    await Task.Delay(HandshakeObserveInterval, cancellationToken);
                    continue;
                }

                await Task.Delay(HandshakeObserveInterval, cancellationToken);

                var verifyResult = await SendGlobalParamReadAsync(InitialGlobalParamIndex, InitialGlobalParamNumber, cancellationToken);
                LogGlobalParamCommandResult($"RecoverRead[{retry}]", verifyResult);

                if (TryGetHandshakeStatus(verifyResult, out var verifyValue) && verifyValue == InitialGlobalParamValue)
                {
                    Interlocked.Exchange(ref _handshakeCompleted, 1);
                    _logger.LogCritical("ACI自动补写2成功，第 {Retry} 次回读确认值={ParamValue}。", retry, verifyValue);
                    return true;
                }

                _logger.LogError("ACI自动补写2第 {Retry} 次后回读仍未确认到2，当前值={ParamValue}。", retry, verifyValue);
            }

            return false;
        }

        private Task<AciCommandData> SendGlobalParamReadAsync(int index, int number, CancellationToken cancellationToken)
        {
            return SendCommandAsync(callback => SendGlobalParamRead(callback, index, number), cancellationToken);
        }

        private Task<AciCommandData> SendGlobalParamWriteAsync(int index, int number, int[] values, CancellationToken cancellationToken)
        {
            return SendCommandAsync(callback => SendGlobalParamWrite(callback, index, number, values), cancellationToken);
        }

        private async Task<AciCommandData> SendCommandAsync(Func<AciCommandCallBack, AciCommandData> sendFunc, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<AciCommandData>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            sendFunc(data => tcs.TrySetResult(data));
            return await tcs.Task;
        }

        private bool WaitForCommandAcknowledged(AciCommandData commandData, int timeoutMilliseconds)
        {
            var stopwatch = Stopwatch.StartNew();

            while (!commandData.Acknowledged)
            {
                if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
                {
                    return false;
                }

                Thread.Sleep(10);
            }

            return true;
        }

        public void AciEventAdd(AciEvent e)
        {
            AciEventQueue.Enqueue(e);

            if (AciEventCount++ > 100)
            {
                AciEvent remove;
                if (AciEventQueue.TryDequeue(out remove))
                {
                    remove.SetOverDate();
                    AciEventCount--;
                }
            }
        }

        public AciCommandData SendGlobalParamRead(AciCommandCallBack callback, int index, int number)
        {
            return AciClient.SendGlobalParamRead(callback, index, number);
        }

        public AciCommandData SendGlobalParamWrite(AciCommandCallBack callback, int index, int number, int[] vals)
        {
            return AciClient.SendGlobalParamWrite(callback, index, number, vals);
        }

        public AciCommandData SendHostAcknowledge(AciCommandCallBack callback, int oix, int hostack, int ack1, int ack2)
        {
            return SendLocalParamInsert(callback, oix, 18, new int[] { hostack, ack1, ack2 });
        }

        public AciCommandData SendLocalParamInsert(AciCommandCallBack callback, int oix, int pix, int[] pvals)
        {
            return AciClient.SendLocalParamInsert(callback, oix, pix, pvals);
        }

        public AciCommandData SendOrderInitial(AciCommandCallBack callback, int key, int trp, int pri, int[] vals)
        {
            if (Volatile.Read(ref _handshakeCompleted) != 1)
            {
                _logger.LogError("下发任务前检查握手流程尚未完成，跳过下发，Key: {Key}。", key);
                var notReadyData = new AciCommandData(null, callback);
                notReadyData.Cancel(AciCommandErrorCode.DataError);
                return notReadyData;
            }

            var readCmd = SendGlobalParamRead(null, InitialGlobalParamIndex, InitialGlobalParamNumber);
            if (WaitForCommandAcknowledged(readCmd, 2000))
            {
                if (TryGetHandshakeStatus(readCmd, out var paramValue) && paramValue == InitialGlobalParamValue)
                {
                    return AciClient.SendOrderInitial(callback, key, trp, pri, vals);
                }

                _logger.LogError("下发任务前检查握手状态不为2，当前值: {ParamValue}，跳过下发，Key: {Key}。", paramValue, key);
            }
            else
            {
                _logger.LogError("下发任务前检查握手状态超时，跳过下发，Key: {Key}。", key);
            }

            var errData = new AciCommandData(null, callback);
            errData.Cancel(AciCommandErrorCode.DataError);
            return errData;
        }

        public AciCommandData SendOrderDeleteViaOrder(AciCommandCallBack callback, int index)
        {
            return AciClient.SendOrderDeleteViaOrder(callback, index);
        }
    }
}
