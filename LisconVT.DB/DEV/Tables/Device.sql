CREATE TABLE [DEV].[Device] (
    [IDNO]         VARCHAR (20) NOT NULL,
    [Plate]        VARCHAR (20) NOT NULL,
    [DevGroupID]   INT          NULL,
    [CreateUserID] INT          NOT NULL,
    [CreateTime]   DATETIME     CONSTRAINT [DF_Device_CreateTime] DEFAULT (getutcdate()) NOT NULL,
    [UpdateUserID] INT          NOT NULL,
    [UpdateTime]   DATETIME     CONSTRAINT [DF_Device_UpdateTime] DEFAULT (getutcdate()) NOT NULL,
    CONSTRAINT [PK_Device] PRIMARY KEY CLUSTERED ([IDNO] ASC),
    CONSTRAINT [FK_Device_DevGroup] FOREIGN KEY ([DevGroupID]) REFERENCES [DEV].[DevGroup] ([ID])
);



