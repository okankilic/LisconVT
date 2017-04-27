CREATE TABLE [DEV].[DeviceLog] (
    [ID]        INT             IDENTITY (1, 1) NOT NULL,
    [LogTime]   DATETIME        CONSTRAINT [DF_DeviceLog_LogTime] DEFAULT (getutcdate()) NOT NULL,
    [LogType]   INT             NOT NULL,
    [DevIDNO]   VARCHAR (20)    NOT NULL,
    [Latitude]  NUMERIC (18, 6) NOT NULL,
    [Longitude] NUMERIC (18, 6) NOT NULL,
    [Altitude]  NUMERIC (18, 2) NOT NULL,
    [Speed]     INT             NOT NULL,
    [Course]    INT             NOT NULL,
    CONSTRAINT [PK_DeviceLog] PRIMARY KEY CLUSTERED ([ID] ASC),
    CONSTRAINT [FK_DeviceLog_Device] FOREIGN KEY ([DevIDNO]) REFERENCES [DEV].[Device] ([IDNO]),
    CONSTRAINT [IX_DeviceLog] UNIQUE NONCLUSTERED ([LogTime] ASC, [LogType] ASC, [DevIDNO] ASC)
);



