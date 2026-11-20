SET XACT_ABORT ON;
BEGIN TRANSACTION;
ALTER TABLE [Panellists] ADD [PhotoData] varbinary(max) NULL, [PhotoContentType] nvarchar(32) NULL, [PhotoRevision] bigint NULL;
ALTER TABLE [Questions] ADD [SpeakerId] uniqueidentifier NULL;
ALTER TABLE [Questions] ADD CONSTRAINT [FK_Questions_Panellists_SpeakerId] FOREIGN KEY ([SpeakerId]) REFERENCES [Panellists]([Id]) ON DELETE SET NULL;
CREATE INDEX [IX_Questions_SpeakerId] ON [Questions]([SpeakerId]);
ALTER TABLE [Boards] ADD
    [SpeakerSeconds] int NOT NULL CONSTRAINT [DF_Boards_SpeakerSeconds] DEFAULT 120,
    [GroupSeconds] int NOT NULL CONSTRAINT [DF_Boards_GroupSeconds] DEFAULT 300,
    [GroupDiscussion] bit NOT NULL CONSTRAINT [DF_Boards_GroupDiscussion] DEFAULT 0,
    [TimerStartedAt] datetimeoffset NULL,
    [TimerDurationSeconds] int NULL;
ALTER TABLE [Boards] ADD CONSTRAINT [CK_Boards_Durations] CHECK ([SpeakerSeconds] BETWEEN 5 AND 3600 AND [GroupSeconds] BETWEEN 5 AND 3600);
ALTER TABLE [Boards] ADD CONSTRAINT [CK_Boards_Timer] CHECK (
    ([TimerStartedAt] IS NULL AND [TimerDurationSeconds] IS NULL) OR
    ([TimerStartedAt] IS NOT NULL AND [TimerDurationSeconds] IS NOT NULL AND [TimerDurationSeconds] BETWEEN 5 AND 3600));
COMMIT;
