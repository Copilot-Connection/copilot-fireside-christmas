SET XACT_ABORT ON;
BEGIN TRANSACTION;
CREATE TABLE QuestionSpeakerOrders (
    QuestionId uniqueidentifier NOT NULL,
    PanellistId uniqueidentifier NOT NULL,
    SortOrder int NOT NULL,
    CONSTRAINT PK_QuestionSpeakerOrders PRIMARY KEY (QuestionId, PanellistId),
    CONSTRAINT CK_QuestionSpeakerOrders_Order CHECK(SortOrder >= 0),
    CONSTRAINT FK_QuestionSpeakerOrders_Questions FOREIGN KEY (QuestionId) REFERENCES Questions(Id) ON DELETE CASCADE,
    CONSTRAINT FK_QuestionSpeakerOrders_Panellists FOREIGN KEY (PanellistId) REFERENCES Panellists(Id)
);
CREATE INDEX IX_QuestionSpeakerOrders_PanellistId ON QuestionSpeakerOrders(PanellistId);
COMMIT;
