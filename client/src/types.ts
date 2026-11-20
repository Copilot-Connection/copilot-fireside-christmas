export type Panellist = {
  id: string; name: string; role: string; bio: string; visible: boolean; sortOrder: number;
};
export type Question = {
  id: string; text: string; category: string; visible: boolean; answered: boolean;
  sortOrder: number; score: number; myScore: number | null;
};
export type ReactionKind = 'like' | 'dislike' | 'epic';
export type LiveReaction = { id: string; questionId: string; kind: ReactionKind; expires: number };
export type Board = {
  title: string; subtitle: string; votingOpen: boolean; currentQuestionId: string | null;
  revision: number; panellists: Panellist[]; questions: Question[];
  landingImageUrl: string; landingImageDescription: string; hasCustomLandingImage: boolean;
};
export type Session = {
  participantName: string | null; isAdmin: boolean; signedIn: boolean; canVote: boolean;
  adminName: string | null; demo: boolean; csrfToken: string;
};
