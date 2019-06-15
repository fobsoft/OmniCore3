﻿using OmniCore.Model.Enums;
using OmniCore.Model.Eros.Data;
using OmniCore.Model.Exceptions;
using OmniCore.Model.Interfaces;
using OmniCore.Model.Interfaces.Data;
using OmniCore.Model.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OmniCore.Model.Eros
{
    public class ErosPodManager : IPodManager
    {
        public ErosPod ErosPod { get; private set; }
        public IPod Pod { get => ErosPod; }

        readonly IMessageExchangeProvider MessageExchangeProvider;
        readonly SemaphoreSlim ConversationMutex;

        private Nonce nonce;
        public Nonce Nonce
        {
            get
            {
                if (nonce == null)
                {
                    if (Pod?.Lot != null && Pod?.Serial != null)
                        nonce = new Nonce((ErosPod)Pod);
                }
                return nonce;
            }
        }

        public ErosPodManager(ErosPod pod, IMessageExchangeProvider messageExchangeProvider)
        {
            ErosPod = pod;
            MessageExchangeProvider = messageExchangeProvider;
            ConversationMutex = new SemaphoreSlim(1);
        }

        public async Task<IConversation> StartConversation(int timeoutMilliseconds = 0, RequestSource source = RequestSource.OmniCoreUser)
        {
            if (timeoutMilliseconds == 0)
            {
                await ConversationMutex.WaitAsync();
            }
            else
            {
                if (!await ConversationMutex.WaitAsync(timeoutMilliseconds).NoSync())
                    return null;
            }

            Pod.ActiveConversation = new ErosConversation(ConversationMutex, Pod) { RequestSource = source };
            return Pod.ActiveConversation;
        }

        private ErosMessageExchangeParameters GetStandardParameters()
        {
            return new ErosMessageExchangeParameters() { Nonce = Nonce, AllowAutoLevelAdjustment = true };
        }

        private async Task<bool> PerformExchange(IMessage requestMessage, IMessageExchangeParameters messageExchangeParameters,
                    IConversation conversation = null, IMessageExchangeProgress progress = null)
        {
            var emp = messageExchangeParameters as ErosMessageExchangeParameters;
            if (conversation != null && progress == null)
                progress = conversation.NewExchange(requestMessage);
            try
            {
                progress.Result.RequestTime = DateTime.UtcNow;
                progress.Running = true;
                var messageExchange = await MessageExchangeProvider.GetMessageExchange(messageExchangeParameters, Pod).NoSync();
                await messageExchange.InitializeExchange(progress).NoSync();
                var response = await messageExchange.GetResponse(requestMessage, progress).NoSync();
                messageExchange.ParseResponse(response, Pod, progress);

                if (ErosPod.RuntimeVariables.NonceSync.HasValue)
                {
                    var responseMessage = response as ErosMessage;
                    emp.MessageSequenceOverride = (responseMessage.sequence + 15) % 16;
                    messageExchange = await MessageExchangeProvider.GetMessageExchange(messageExchangeParameters, Pod).NoSync();
                    await messageExchange.InitializeExchange(progress).NoSync();
                    response = await messageExchange.GetResponse(requestMessage, progress).NoSync();
                    messageExchange.ParseResponse(response, Pod, progress);
                    if (ErosPod.RuntimeVariables.NonceSync.HasValue)
                        throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Nonce re-negotiation failed");
                }
                progress.Result.Success = true;
            }
            catch (Exception e)
            {
                progress.SetException(e);
            }
            finally
            {
                progress.Result.ResultTime = DateTime.UtcNow;
                progress.Running = false;
                progress.Finished = true;
                ErosRepository.Instance.Save(ErosPod, progress.Result);
            }

            return progress.Result.Success;
        }

        private async Task<bool> UpdateStatusInternal(IConversation conversation,
            StatusRequestType updateType = StatusRequestType.Standard)
        {
            var request = new ErosMessageBuilder().WithStatus(updateType).Build();
            return await PerformExchange(request, GetStandardParameters(), conversation).NoSync();
        }

        public async Task UpdateStatus(IConversation conversation, 
            StatusRequestType updateType = StatusRequestType.Standard)
        {
            try
            {
                if (!await this.UpdateStatusInternal(conversation, updateType).NoSync())
                    return;
            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public async Task AcknowledgeAlerts(IConversation conversation, byte alertMask)
        {
            try
            {
                Debug.WriteLine($"Acknowledging alerts, bitmask: {alertMask}");
                if (!await UpdateStatusInternal(conversation).NoSync())
                    return;

                AssertImmediateBolusInactive();
                if (Pod.LastStatus.Progress < PodProgress.PairingSuccess)
                    throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod not paired completely yet.");

                if (Pod.LastStatus.Progress == PodProgress.ErrorShuttingDown)
                    throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod is shutting down, cannot acknowledge alerts.");

                if (Pod.LastStatus.Progress == PodProgress.AlertExpiredShuttingDown)
                    throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Acknowledgement period expired, pod is shutting down");

                if (Pod.LastStatus.Progress > PodProgress.AlertExpiredShuttingDown)
                    throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod is not active");

                if ((Pod.LastStatus.AlertMask & alertMask) != alertMask)
                    throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Bitmask is invalid for current alert state");

                var request = new ErosMessageBuilder().WithAcknowledgeAlerts(alertMask).Build();
                if (!await PerformExchange(request, GetStandardParameters(), conversation).NoSync())
                    return;

                if ((Pod.LastStatus.AlertMask & alertMask) != 0)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Alerts not completely acknowledged");

            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public async Task SetTempBasal(IConversation conversation, decimal basalRate, decimal durationInHours)
        {
            try
            {
                // progress.CommandText = $"Set Temp Basal {basalRate}U/h for {durationInHours}h";
                if (!await UpdateStatusInternal(conversation))
                    return;

                AssertRunningStatus();
                AssertImmediateBolusInactive();

                if (Pod.LastStatus.BasalState == BasalState.Temporary)
                {
                    var cancelReq = new ErosMessageBuilder().WithCancelTempBasal().Build();
                    if (!await PerformExchange(cancelReq, GetStandardParameters(), conversation).NoSync())
                        return;
                }

                if (Pod.LastStatus.BasalState == BasalState.Temporary)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod is still executing a temp basal");

                var request = new ErosMessageBuilder().WithTempBasal(basalRate, durationInHours).Build();
                if (!await PerformExchange(request, GetStandardParameters(), conversation).NoSync())
                    return;

                if (Pod.LastStatus.BasalState != BasalState.Temporary)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not start the temp basal");

                Pod.LastTempBasalResult = conversation.CurrentExchange.Result;
            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public async Task Bolus(IConversation conversation, decimal bolusAmount, bool waitForBolusToFinish = true)
        {
            try
            {
                //progress.CommandText = $"Bolusing {bolusAmount}U";
                Debug.WriteLine($"Bolusing {bolusAmount}U");
                if (!await UpdateStatusInternal(conversation).NoSync())
                    return;

                AssertRunningStatus();
                AssertImmediateBolusInactive();

                if (bolusAmount < 0.05m)
                    throw new OmniCoreWorkflowException(FailureType.InvalidParameter, "Cannot bolus less than 0.05U");

                if (bolusAmount % 0.05m != 0)
                    throw new OmniCoreWorkflowException(FailureType.InvalidParameter, "Bolus must be multiples of 0.05U");

                if (bolusAmount > 30m)
                    throw new OmniCoreWorkflowException(FailureType.InvalidParameter, "Cannot bolus more than 30U");

                var request = new ErosMessageBuilder().WithBolus(bolusAmount).Build();
                if (!await PerformExchange(request, GetStandardParameters(), conversation).NoSync())
                    return;

                if (Pod.LastStatus.BolusState != BolusState.Immediate)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not start bolusing");

                if (waitForBolusToFinish)
                {
                    while (Pod.LastStatus.BolusState == BolusState.Immediate)
                    {
                        var tickCount = (int)(Pod.LastStatus.NotDeliveredInsulin / 0.05m);
                        await Task.Delay(tickCount * 2000 + 500, conversation.Token).NoSync();

                        if (conversation.Token.IsCancellationRequested)
                        {
                            var cancelRequest = new ErosMessageBuilder().WithCancelBolus().Build();
                            var cancelResult = await PerformExchange(request, GetStandardParameters(), conversation).NoSync();

                            if (!cancelResult || Pod.LastStatus.BolusState == BolusState.Immediate)
                            {
                                conversation.CancelFailed();
                            }
                            else
                            {
                                break;
                            }
                        }
                        if (!await UpdateStatusInternal(conversation).NoSync())
                            return;
                    }

                    if (conversation.Canceled || conversation.Failed)
                        return;

                    if (Pod.LastStatus.NotDeliveredInsulin != 0)
                        throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Not all insulin was delivered");
                }
            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public async Task Deactivate(IConversation conversation)
        {
            try
            {
                // progress.CommandText = $"Deactivating Pod";
                AssertPaired();

                if (Pod.LastStatus.Progress >= PodProgress.Inactive)
                    throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod already deactivated");

                var request = new ErosMessageBuilder().WithDeactivate().Build();
                if (!await PerformExchange(request, GetStandardParameters(), conversation).NoSync())
                    return;

                if (Pod.LastStatus.Progress != PodProgress.Inactive)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Failed to deactivate");
            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public async Task Pair(IConversation conversation, int utcOffsetMinutes)
        {
            try
            {
                // progress.CommandText = $"Pairing with Pod";

                AssertNotPaired();

                if (Pod.LastStatus == null || Pod.LastStatus.Progress <= PodProgress.TankFillCompleted)
                {
                    var parameters = GetStandardParameters();
                    parameters.AddressOverride = 0xffffffff;
                    parameters.AckAddressOverride = Pod.RadioAddress;
                    parameters.TransmissionLevelOverride = TxPower.A3_BelowNormal;
                    parameters.AllowAutoLevelAdjustment = false;

                    var request = new ErosMessageBuilder().WithAssignAddress(Pod.RadioAddress).Build();
                    if (!await PerformExchange(request, parameters, conversation).NoSync())
                        return;

                    if (Pod.LastStatus == null)
                        throw new OmniCoreWorkflowException(FailureType.RadioRecvTimeout, "Pod did not respond to pairing request");
                    else if (Pod.LastStatus.Progress < PodProgress.TankFillCompleted)
                        throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod is not filled with enough insulin for activation.");

                }

                if (Pod.LastStatus != null && Pod.LastStatus.Progress < PodProgress.PairingSuccess)
                {
                    Pod.ActivationDate = DateTime.UtcNow;
                    var podDate = Pod.ActivationDate.Value + TimeSpan.FromMinutes(utcOffsetMinutes);
                    var parameters = GetStandardParameters();
                    parameters.AddressOverride = 0xffffffff;
                    parameters.AckAddressOverride = Pod.RadioAddress;
                    parameters.TransmissionLevelOverride = TxPower.A3_BelowNormal;
                    parameters.MessageSequenceOverride = 1;
                    parameters.AllowAutoLevelAdjustment = false;

                    var request = new ErosMessageBuilder().WithSetupPod(Pod.Lot.Value, Pod.Serial.Value, Pod.RadioAddress,
                        podDate.Year, (byte)podDate.Month, (byte)podDate.Day,
                        (byte)podDate.Hour, (byte)podDate.Minute).Build();

                    if (!await PerformExchange(request, parameters, conversation).NoSync())
                        return;

                    AssertPaired();
                }
            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public async Task Activate(IConversation conversation)
        {
            try
            {
                // progress.CommandText = $"Activating Pod";
                if (!await UpdateStatusInternal(conversation).NoSync())
                    return;

                if (Pod.LastStatus.Progress > PodProgress.ReadyForInjection)
                    throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod is already activated");

                if (Pod.LastStatus.Progress == PodProgress.PairingSuccess)
                {
                    var parameters = GetStandardParameters();
                    parameters.MessageSequenceOverride = 2;

                    var ac = new AlertConfiguration
                    {
                        activate = true,
                        alert_index = 7,
                        alert_after_minutes = 5,
                        alert_duration = 55,
                        beep_type = BeepType.BipBeepFourTimes,
                        beep_repeat_type = BeepPattern.OnceEveryFiveMinutes
                    };

                    var request = new ErosMessageBuilder()
                        .WithAlertSetup(new List<AlertConfiguration>(new[] { ac }))
                        .Build();

                    if (!await PerformExchange(request, parameters, conversation).NoSync())
                        return;

                    request = new ErosMessageBuilder().WithDeliveryFlags(0, 0).Build();
                    if (!await PerformExchange(request, parameters, conversation).NoSync())
                        return;

                    request = new ErosMessageBuilder().WithPrimeCannula().Build();
                    if (!await PerformExchange(request, parameters, conversation).NoSync())
                        return;

                    if (Pod.LastStatus.Progress != PodProgress.Purging)
                        throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not start priming");

                }

                while (Pod.LastStatus.Progress == PodProgress.Purging)
                {
                    var ticks = (int)(Pod.LastStatus.NotDeliveredInsulin / 0.05m);
                    await Task.Delay(ticks * 1000 + 200);

                    if (!await UpdateStatusInternal(conversation).NoSync())
                        return;
                }

                if (Pod.LastStatus.Progress != PodProgress.ReadyForInjection)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not reach ready for injection state.");

                if (Pod.LastUserSettings?.ExpiryWarningAtMinute != null)
                {
                    //TODO: expiry warning
                }
            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public async Task InjectAndStart(IConversation conversation, decimal[] basalSchedule, int utcOffsetInMinutes)
        {
            try
            {
                // progress.CommandText = $"Starting Pod";
                if (!await UpdateStatusInternal(conversation).NoSync())
                    return;

                if (Pod.LastStatus.Progress >= PodProgress.Running)
                    throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod is already started");

                if (Pod.LastStatus.Progress < PodProgress.ReadyForInjection)
                    throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod is not ready for injection");

                if (Pod.LastStatus.Progress == PodProgress.ReadyForInjection)
                {
                    AssertBasalScheduleValid(basalSchedule);

                    var podDate = DateTime.UtcNow + TimeSpan.FromMinutes(utcOffsetInMinutes);
                    var parameters = GetStandardParameters();
                    parameters.RepeatFirstPacket = true;
                    parameters.CriticalWithFollowupRequired = true;

                    var request = new ErosMessageBuilder()
                        .WithBasalSchedule(basalSchedule, (ushort)podDate.Hour, (ushort)podDate.Minute, (ushort)podDate.Second)
                        .Build();

                    var progress = conversation.NewExchange(request);
                    progress.Result.BasalSchedule = new ErosBasalSchedule()
                    {
                        BasalSchedule = basalSchedule,
                        PodDateTime = podDate,
                        UtcOffset = utcOffsetInMinutes
                    };

                    if (!await PerformExchange(request, parameters, null, progress).NoSync())
                        return;

                    if (Pod.LastStatus.Progress != PodProgress.BasalScheduleSet)
                        throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not acknowledge basal schedule");
                }

                if (Pod.LastStatus.Progress == PodProgress.BasalScheduleSet)
                {
                    var acs = new List<AlertConfiguration>(new[]
                    {
                        new AlertConfiguration()
                        {
                            activate = false,
                            alert_index = 7,
                            alert_duration = 0,
                            alert_after_minutes = 0,
                            beep_type = BeepType.NoSound,
                            beep_repeat_type = BeepPattern.Once
                        },

                        new AlertConfiguration()
                        {
                            activate = false,
                            alert_index = 0,
                            alert_duration = 0,
                            alert_after_minutes = 15,
                            trigger_auto_off = true,
                            beep_type = BeepType.BipBeepFourTimes,
                            beep_repeat_type = BeepPattern.OnceEveryMinuteForFifteenMinutes
                        }

                    });

                    var request = new ErosMessageBuilder().WithAlertSetup(acs).Build();
                    if (!await PerformExchange(request, GetStandardParameters(), conversation).NoSync())
                        return;

                    request = new ErosMessageBuilder().WithInsertCannula().Build();
                    if (!await PerformExchange(request, GetStandardParameters(), conversation).NoSync())
                        return;

                    if (Pod.LastStatus.Progress != PodProgress.Priming)
                        throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not start priming the cannula for insertion");

                    Pod.InsertionDate = DateTime.UtcNow;
                }

                while (Pod.LastStatus.Progress == PodProgress.Priming)
                {
                    var ticks = (int)(Pod.LastStatus.NotDeliveredInsulin / 0.05m);
                    await Task.Delay(ticks * 1000 + 200).NoSync();

                    if (!await UpdateStatusInternal(conversation).NoSync())
                        return;
                }

                if (Pod.LastStatus.Progress != PodProgress.Running)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not enter the running state");

                Pod.ReservoirUsedForPriming = Pod.LastStatus.DeliveredInsulin;

            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public async Task ConfigureAlerts(IConversation conversation, AlertConfiguration[] alertConfigurations)
        {
            throw new NotImplementedException();
        }

        public async Task CancelBolus(IConversation conversation)
        {
            try
            {
                AssertRunningStatus();
                AssertImmediateBolusActive();

                var request = new ErosMessageBuilder().WithCancelBolus().Build();
                if (!await PerformExchange(request, GetStandardParameters(), conversation).NoSync())
                    return;

                if (Pod.LastStatus.BolusState != BolusState.Inactive)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not cancel the bolus");
            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public async Task CancelTempBasal(IConversation conversation)
        {
            try
            {
                if (!await UpdateStatusInternal(conversation).NoSync())
                    return;

                AssertRunningStatus();
                AssertImmediateBolusInactive();

                if (Pod.LastStatus.BasalState == BasalState.Temporary)
                {
                    var request = new ErosMessageBuilder().WithCancelTempBasal().Build();
                    if (!await PerformExchange(request, GetStandardParameters(), conversation).NoSync())
                        return;
                }

                if (Pod.LastStatus.BasalState != BasalState.Scheduled)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not cancel the temp basal");

                Pod.LastTempBasalResult = null;
            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public Task StartExtendedBolus(IConversation conversation, decimal bolusAmount, decimal durationInHours)
        {
            throw new NotImplementedException();
        }

        public Task CancelExtendedBolus(IConversation conversation)
        {
            throw new NotImplementedException();
        }

        public async Task SetBasalSchedule(IConversation conversation, decimal[] schedule, int utcOffsetInMinutes)
        {
            try
            {
                if (!await UpdateStatusInternal(conversation).NoSync())
                    return;

                AssertRunningStatus();
                AssertImmediateBolusInactive();

                if (Pod.LastStatus.BasalState == BasalState.Temporary)
                {
                    var cancelReq = new ErosMessageBuilder().WithCancelTempBasal().Build();
                    if (!await PerformExchange(cancelReq, GetStandardParameters(), conversation).NoSync())
                        return;
                }

                if (Pod.LastStatus.BasalState == BasalState.Temporary)
                    throw new OmniCoreWorkflowException(FailureType.PodResponseUnexpected, "Pod did not cancel the temp basal");

                AssertBasalScheduleValid(schedule);

                var podDate = DateTime.UtcNow + TimeSpan.FromMinutes(utcOffsetInMinutes);
                var parameters = GetStandardParameters();
                //parameters.RepeatFirstPacket = true;
                parameters.CriticalWithFollowupRequired = false;

                var request = new ErosMessageBuilder()
                    .WithBasalSchedule(schedule, (ushort)podDate.Hour, (ushort)podDate.Minute, (ushort)podDate.Second)
                    .Build();

                var progress = conversation.NewExchange(request);
                progress.Result.BasalSchedule = new ErosBasalSchedule()
                {
                    BasalSchedule = schedule,
                    PodDateTime = podDate,
                    UtcOffset = utcOffsetInMinutes
                };

                if (!await PerformExchange(request, parameters, null, progress).NoSync())
                    return;

            }
            catch (Exception e)
            {
                conversation.Exception = e;
            }
        }

        public Task SuspendBasal(IConversation conversation)
        {
            throw new NotImplementedException();
        }


        private void AssertBasalScheduleValid(decimal[] basalSchedule)
        {
            if (basalSchedule.Length != 48)
                throw new OmniCoreWorkflowException(FailureType.InvalidParameter, "Invalid basal schedule, it must contain 48 half hour elements.");

            foreach(var entry in basalSchedule)
            {
                if (entry % 0.05m != 0)
                    throw new OmniCoreWorkflowException(FailureType.InvalidParameter, "Basal schedule entries must be multiples of 0.05U");

                if (entry < 0.05m)
                    throw new OmniCoreWorkflowException(FailureType.InvalidParameter, "Basal schedule entry cannot be less than 0.05U");

                if (entry > 30m)
                    throw new OmniCoreWorkflowException(FailureType.InvalidParameter, "Basal schedule entry cannot be more than 30U");
            }
        }

        private void AssertImmediateBolusInactive()
        {
            if (Pod.LastStatus != null && Pod.LastStatus.BolusState == BolusState.Immediate)
                throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Bolus operation in progress");
        }

        private void AssertImmediateBolusActive()
        {
            if (Pod.LastStatus != null && Pod.LastStatus.BolusState != BolusState.Immediate)
                throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "No bolus operation in progress");
        }

        private void AssertNotPaired()
        {
            if (Pod.LastStatus != null && Pod.LastStatus.Progress >= PodProgress.PairingSuccess)
                throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod is already paired");
        }

        private void AssertPaired()
        {
            if (Pod.LastStatus == null || Pod.LastStatus.Progress < PodProgress.PairingSuccess)
                throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod is not paired");
        }

        private void AssertRunningStatus()
        {
            if (Pod.LastStatus == null || Pod.LastStatus.Progress < PodProgress.Running)
                throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod is not yet running");

            if (Pod.LastStatus == null || Pod.LastStatus.Progress > PodProgress.RunningLow)
                throw new OmniCoreWorkflowException(FailureType.PodStateInvalidForCommand, "Pod is not running");
        }
    }
}
