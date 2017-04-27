CREATE TABLE [USR].[User] (
    [ID]           INT           IDENTITY (1, 1) NOT NULL,
    [FullName]     NVARCHAR (50) NOT NULL,
    [Phone]        VARCHAR (50)  NULL,
    [Email]        VARCHAR (50)  NULL,
    [CreateUserID] INT           NOT NULL,
    [CreateTime]   DATETIME      CONSTRAINT [DF_User_CreateTime] DEFAULT (getutcdate()) NOT NULL,
    [UpdateUserID] INT           NOT NULL,
    [UpdateTime]   DATETIME      CONSTRAINT [DF_User_UpdateTime] DEFAULT (getutcdate()) NOT NULL,
    CONSTRAINT [PK_User] PRIMARY KEY CLUSTERED ([ID] ASC)
);

