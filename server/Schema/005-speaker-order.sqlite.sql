BEGIN IMMEDIATE;
CREATE TABLE QuestionSpeakerOrders (
    QuestionId TEXT NOT NULL REFERENCES Questions(Id) ON DELETE CASCADE,
    PanellistId TEXT NOT NULL REFERENCES Panellists(Id),
    SortOrder INTEGER NOT NULL CHECK(SortOrder >= 0),
    PRIMARY KEY (QuestionId, PanellistId)
);
CREATE INDEX IX_QuestionSpeakerOrders_PanellistId ON QuestionSpeakerOrders(PanellistId);
COMMIT;
