export type Panellist = {
  id: string; name: string; role: string; bio: string; visible: boolean; sortOrder: number;
};
export type Question = {
  id: string; text: string; category: string; visible: boolean; answered: boolean;
  sortOrder: number; votes: number; hasVoted: boolean;
};
export type Board = {
  title: string; subtitle: string; votingOpen: boolean; currentQuestionId: string | null;
  revision: number; panellists: Panellist[]; questions: Question[];
};
export type Session = {
  participantName: string | null; isAdmin: boolean; signedIn: boolean;
  adminName: string | null; demo: boolean; csrfToken: string;
};
