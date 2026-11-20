BEGIN IMMEDIATE;
ALTER TABLE "Boards" ADD COLUMN "LandingImageData" BLOB NULL;
ALTER TABLE "Boards" ADD COLUMN "LandingImageContentType" TEXT NULL;
ALTER TABLE "Boards" ADD COLUMN "LandingImageRevision" INTEGER NULL;
ALTER TABLE "Boards" ADD COLUMN "LandingImageDescription" TEXT NOT NULL DEFAULT 'Copilot Fireside Chat, 16 December at 16:00 GMT. Caroline Kallin, Claire Edgson, Mark Rackley and Jon Jarvis. Register at events.empowering.cloud.';
COMMIT;
