SET XACT_ABORT ON;
BEGIN TRANSACTION;
ALTER TABLE [Boards] ADD [LandingImageData] varbinary(max) NULL;
ALTER TABLE [Boards] ADD [LandingImageContentType] nvarchar(32) NULL;
ALTER TABLE [Boards] ADD [LandingImageRevision] bigint NULL;
ALTER TABLE [Boards] ADD [LandingImageDescription] nvarchar(300) NOT NULL
    CONSTRAINT [DF_Boards_LandingImageDescription] DEFAULT N'Copilot Fireside Chat, 16 December at 16:00 GMT. Caroline Kallin, Claire Edgson, Mark Rackley and Jon Jarvis. Register at events.empowering.cloud.';
COMMIT;
