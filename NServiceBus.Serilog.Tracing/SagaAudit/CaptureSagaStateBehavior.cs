﻿using System;
using System.Threading.Tasks;
using NServiceBus.Pipeline;
using NServiceBus.Sagas;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;

namespace NServiceBus.Serilog.Tracing
{

    class CaptureSagaStateBehavior : Behavior<IInvokeHandlerContext>
    {
        SagaUpdatedMessage sagaAudit;
        ILogger logger;
        MessageTemplate messageTemplate;

        public CaptureSagaStateBehavior(LogBuilder logBuilder)
        {
            var templateParser = new MessageTemplateParser();
            messageTemplate = templateParser.Parse("Saga execution '{SagaType}' '{SagaId}'.");

            logger = logBuilder.GetLogger("NServiceBus.Serilog.SagaAudit");
        }

        public override async Task Invoke(IInvokeHandlerContext context, Func<Task> next)
        {
            var saga = context.MessageHandler.Instance as Saga;
            if (saga == null)
            {
                await next().ConfigureAwait(false);
                return; // Message was not handled by the saga
            }

            if (!logger.IsEnabled(LogEventLevel.Information))
            {
                await next().ConfigureAwait(false);
                return;
            }

            sagaAudit = new SagaUpdatedMessage
            {
                StartTime = DateTime.UtcNow
            };
            context.Extensions.Set(sagaAudit);
            await next()
                .ConfigureAwait(false);
            var activeSagaInstance = context.Extensions.Get<ActiveSagaInstance>();
            sagaAudit.SagaType = activeSagaInstance.Instance.GetType().FullName;

            sagaAudit.FinishTime = DateTime.UtcNow;
            AuditSaga(activeSagaInstance, context);
        }

        void AuditSaga(ActiveSagaInstance activeSagaInstance, IInvokeHandlerContext context)
        {
            string messageId;
            var saga = activeSagaInstance.Instance;
            if (!context.Headers.TryGetValue(Headers.MessageId, out messageId))
            {
                return;
            }

            var headers = context.Headers;
            var originatingMachine = headers["NServiceBus.OriginatingMachine"];
            var originatingEndpoint = headers[Headers.OriginatingEndpoint];
            var intent = context.MessageIntent();

            var initiator = new SagaChangeInitiator
            {
                IsSagaTimeoutMessage = context.IsTimeoutMessage(),
                InitiatingMessageId = messageId,
                OriginatingMachine = originatingMachine,
                OriginatingEndpoint = originatingEndpoint,
                MessageType = context.MessageMetadata.MessageType.FullName,
                TimeSent = context.TimeSent(),
                Intent = intent
            };
            sagaAudit.IsNew = activeSagaInstance.IsNew;
            sagaAudit.IsCompleted = saga.Completed;
            sagaAudit.SagaId = saga.Entity.Id;

            AssignSagaStateChangeCausedByMessage(context);

            logger.WriteInfo(
                messageTemplate: messageTemplate,
                properties: new[]
                {
                    new LogEventProperty("SagaType", new ScalarValue(sagaAudit.SagaType)),
                    new LogEventProperty("SagaId", new ScalarValue(sagaAudit.SagaId)),
                    new LogEventProperty("StartTime", new ScalarValue(sagaAudit.StartTime)),
                    new LogEventProperty("FinishTime", new ScalarValue(sagaAudit.FinishTime)),
                    new LogEventProperty("IsCompleted", new ScalarValue(sagaAudit.IsCompleted)),
                    new LogEventProperty("IsNew", new ScalarValue(sagaAudit.IsNew)),
                    new LogEventProperty("SagaType", new ScalarValue(sagaAudit.SagaType)),
                    logger.BindProperty("Initiator", initiator),
                    logger.BindProperty("ResultingMessages", sagaAudit.ResultingMessages),
                    logger.BindProperty("Entity", saga.Entity),
                });
        }


        void AssignSagaStateChangeCausedByMessage(IInvokeHandlerContext context)
        {
            string sagaStateChange;
            if (!context.Headers.TryGetValue("NServiceBus.Serilog.Tracing.SagaStateChange", out sagaStateChange))
            {
                sagaStateChange = string.Empty;
            }

            var statechange = "Updated";
            if (sagaAudit.IsNew)
            {
                statechange = "New";
            }
            if (sagaAudit.IsCompleted)
            {
                statechange = "Completed";
            }

            if (!string.IsNullOrEmpty(sagaStateChange))
            {
                sagaStateChange += ";";
            }
            sagaStateChange += $"{sagaAudit.SagaId}:{statechange}";

            context.Headers["NServiceBus.Serilog.Tracing.SagaStateChange"] = sagaStateChange;
        }


        public class Registration : RegisterStep
        {
            public Registration()
                : base("SerilogCaptureSagaState", typeof(CaptureSagaStateBehavior), "Records saga state changes")
            {
                InsertBefore("InvokeSaga");
            }
        }
    }

}