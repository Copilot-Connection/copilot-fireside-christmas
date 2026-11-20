CREATE TABLE IF NOT EXISTS "AudienceAccounts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_AudienceAccounts" PRIMARY KEY,
    "EventId" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Username" TEXT NOT NULL,
    "PasswordHash" TEXT NOT NULL,
    "SecurityStamp" TEXT NOT NULL,
    "FailedAttempts" INTEGER NOT NULL,
    "LockedUntil" TEXT NULL,
    "Version" INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_AudienceAccounts_UserId" ON "AudienceAccounts" ("UserId");
