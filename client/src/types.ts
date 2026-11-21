export type Panellist = {
  id: string; name: string; role: string; type: 'guest' | 'host'; bio: string; visible: boolean; sortOrder: number; photoUrl: string | null;
};
export type Question = {
  id: string; text: string; category: string; visible: boolean; answered: boolean;
  sortOrder: number; score: number; myScore: number | null; speakerId: string | null; speakerIds: string[];
};
export type ReactionKind = 'like' | 'dislike' | 'epic';
export type LiveReaction = { id: string; questionId: string; kind: ReactionKind; expires: number };
export type Board = {
  title: string; subtitle: string; votingOpen: boolean; currentQuestionId: string | null;
  revision: number; panellists: Panellist[]; questions: Question[];
  landingImageUrl: string; landingImageDescription: string; hasCustomLandingImage: boolean;
  speakerSeconds: number; groupSeconds: number; groupDiscussion: boolean;
  timerStartedAt: string | null; timerDurationSeconds: number | null; serverTime: string;
  scoreboardVisible: boolean; scoreboard?: Scoreboard;
};
export type SpeakerPoints = { speakerId: string; points: number };
export type QuestionScores = { id: string; text: string; scores: SpeakerPoints[]; unassignedPoints: number };
export type SpeakerTotal = { id: string; name: string; points: number; removed: boolean };
export type Scoreboard = { speakers: SpeakerTotal[]; questions: QuestionScores[] };
export type Session = {
  participantName: string | null; isAdmin: boolean; signedIn: boolean; canVote: boolean;
  adminName: string | null; demo: boolean; csrfToken: string;
};
