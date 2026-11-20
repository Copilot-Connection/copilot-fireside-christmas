BEGIN IMMEDIATE;
ALTER TABLE "Votes" RENAME TO "Votes_Legacy";
CREATE TABLE "Votes" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Votes" PRIMARY KEY,
    "QuestionId" TEXT NOT NULL,
    "ParticipantId" TEXT NOT NULL,
    "Delta" INTEGER NOT NULL,
    CONSTRAINT "CK_Votes_Delta" CHECK ("Delta" IN (-1, 1, 3)),
    CONSTRAINT "FK_Votes_Questions_QuestionId" FOREIGN KEY ("QuestionId") REFERENCES "Questions" ("Id") ON DELETE CASCADE
);
INSERT INTO "Votes" ("Id", "QuestionId", "ParticipantId", "Delta")
SELECT lower(hex(randomblob(16))), "QuestionId", "ParticipantId", 1 FROM "Votes_Legacy";
DROP TABLE "Votes_Legacy";
CREATE INDEX "IX_Votes_QuestionId" ON "Votes" ("QuestionId");
COMMIT;
