CREATE TABLE [DEV].[DeviceRuntime] (
    [DevIDNO]      VARCHAR (20)    NOT NULL,
    [IsOnline]     BIT             NOT NULL,
    [GpsTime]      DATETIME        NOT NULL,
    [Latitude]     NUMERIC (18, 6) NOT NULL,
    [Longitude]    NUMERIC (18, 6) NOT NULL,
    [Altitude]     NUMERIC (18, 2) NOT NULL,
    [Speed]        INT             NOT NULL,
    [Course]       INT             NOT NULL,
    [CreateUserID] INT             NOT NULL,
    [CreateTime]   DATETIME        CONSTRAINT [DF_DeviceRuntime_CreateTime] DEFAULT (getutcdate()) NOT NULL,
    [UpdateUserID] INT             NOT NULL,
    [UpdateTime]   DATETIME        CONSTRAINT [DF_DeviceRuntime_UpdateTime] DEFAULT (getutcdate()) NOT NULL,
    CONSTRAINT [PK_DeviceRuntime] PRIMARY KEY CLUSTERED ([DevIDNO] ASC),
    CONSTRAINT [FK_DeviceRuntime_Device] FOREIGN KEY ([DevIDNO]) REFERENCES [DEV].[Device] ([IDNO])
);

