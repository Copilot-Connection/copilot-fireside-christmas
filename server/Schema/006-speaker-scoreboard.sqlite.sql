-- Apply once with writes stopped and a verified backup.
-- User-directed reset: discard pre-scoreboard votes only. Never rerun to reset new votes.
BEGIN IMMEDIATE;
ALTER TABLE Boards ADD COLUMN ScoreboardInitializedAt TEXT NULL;
ALTER TABLE Boards ADD COLUMN ScoreboardVisible INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Votes ADD COLUMN SpeakerId TEXT NULL;
CREATE INDEX IX_Votes_QuestionId_SpeakerId ON Votes(QuestionId, SpeakerId);
CREATE TABLE ScoreAdjustments (
    Id TEXT NOT NULL PRIMARY KEY,
    QuestionId TEXT NOT NULL REFERENCES Questions(Id) ON DELETE CASCADE,
    SpeakerId TEXT NOT NULL,
    Delta INTEGER NOT NULL CHECK(Delta <> 0),
    ActorId TEXT NOT NULL,
    ChangedAt TEXT NOT NULL,
    Reason TEXT NOT NULL
);
CREATE INDEX IX_ScoreAdjustments_QuestionId_SpeakerId ON ScoreAdjustments(QuestionId, SpeakerId);
DELETE FROM Votes WHERE EXISTS(SELECT 1 FROM Boards WHERE ScoreboardInitializedAt IS NULL);
UPDATE Boards SET ScoreboardInitializedAt = strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now') WHERE ScoreboardInitializedAt IS NULL;
COMMIT;
