﻿using System.Threading.Tasks;
using NServiceBus;
using NServiceBus.Logging;

public class CreateUserSaga : Saga<MySagaData>, 
    IAmStartedByMessages<CreateUser>
{
    static ILog log = LogManager.GetLogger(typeof (CreateUserSaga));

    protected override void ConfigureHowToFindSaga(SagaPropertyMapper<MySagaData> mapper)
    {
        mapper.ConfigureMapping<CreateUser>(m => m.UserName)
            .ToSaga(s=>s.UserName);
    }

    public Task Handle(CreateUser message, IMessageHandlerContext context)
    {
        Data.UserName = message.UserName;
        log.InfoFormat("User created. Message: {@Message}", message);
        var userCreated = new UserCreated
        {
            UserName = message.UserName
        };
        MarkAsComplete();
        return context.SendLocal(userCreated);
    }
}