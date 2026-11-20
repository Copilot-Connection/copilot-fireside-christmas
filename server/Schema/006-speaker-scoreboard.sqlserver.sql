-- Apply once with writes stopped and a verified backup.
-- User-directed reset: discard pre-scoreboard votes only. Never rerun to reset new votes.
SET XACT_ABORT ON;
IF COL_LENGTH('Boards', 'ScoreboardInitializedAt') IS NOT NULL
    THROW 50000, 'Scoreboard schema already initialized. Do not rerun the points reset.', 1;
BEGIN TRANSACTION;
ALTER TABLE Boards ADD ScoreboardInitializedAt datetimeoffset NULL;
ALTER TABLE Boards ADD ScoreboardVisible bit NOT NULL CONSTRAINT DF_Boards_ScoreboardVisible DEFAULT 0;
ALTER TABLE Votes ADD SpeakerId uniqueidentifier NULL;
CREATE INDEX IX_Votes_QuestionId_SpeakerId ON Votes(QuestionId, SpeakerId);
CREATE TABLE ScoreAdjustments (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_ScoreAdjustments PRIMARY KEY,
    QuestionId uniqueidentifier NOT NULL,
    SpeakerId uniqueidentifier NOT NULL,
    Delta bigint NOT NULL CONSTRAINT CK_ScoreAdjustments_Delta CHECK(Delta <> 0),
    ActorId uniqueidentifier NOT NULL,
    ChangedAt datetimeoffset NOT NULL,
    Reason nvarchar(200) NOT NULL,
    CONSTRAINT FK_ScoreAdjustments_Questions FOREIGN KEY (QuestionId) REFERENCES Questions(Id) ON DELETE CASCADE
);
CREATE INDEX IX_ScoreAdjustments_QuestionId_SpeakerId ON ScoreAdjustments(QuestionId, SpeakerId);
EXEC(N'DELETE FROM Votes WHERE EXISTS(SELECT 1 FROM Boards WHERE ScoreboardInitializedAt IS NULL);
UPDATE Boards SET ScoreboardInitializedAt = SYSUTCDATETIME() WHERE ScoreboardInitializedAt IS NULL;');
COMMIT;
