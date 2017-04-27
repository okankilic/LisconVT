CREATE TABLE [DEV].[DevGroup] (
    [ID]           INT            IDENTITY (1, 1) NOT NULL,
    [DevGroupID]   INT            NULL,
    [DevGroupName] NVARCHAR (50)  NOT NULL,
    [Remarks]      NVARCHAR (100) NULL,
    [CreateUserID] INT            NOT NULL,
    [CreateTime]   DATETIME       CONSTRAINT [DF_DevGroup_CreateTime] DEFAULT (getutcdate()) NOT NULL,
    [UpdateUserID] INT            NOT NULL,
    [UpdateTime]   DATETIME       CONSTRAINT [DF_DevGroup_UpdateTime] DEFAULT (getutcdate()) NOT NULL,
    CONSTRAINT [PK_DevGroup] PRIMARY KEY CLUSTERED ([ID] ASC),
    CONSTRAINT [FK_DevGroup_DevGroup] FOREIGN KEY ([DevGroupID]) REFERENCES [DEV].[DevGroup] ([ID])
);

