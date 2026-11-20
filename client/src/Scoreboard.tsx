import type { Panellist, QuestionScores, Scoreboard as Scores, SpeakerTotal } from './types';
import { PanellistPhoto } from './App';

export function Scoreboard({ scores, people = [], onEdit }: {
  scores: Scores;
  people?: Panellist[];
  onEdit?: (question: QuestionScores, speaker: SpeakerTotal, points: number) => void;
}) {
  if (!scores.speakers.length && !scores.questions.length)
    return <p className="empty-state">Scores will appear when the host publishes speakers and questions.</p>;
  return <div className="scoreboard">
    <h3>Speaker totals</h3>
    <dl className="scoreboard-totals">{scores.speakers.map(speaker => <div key={speaker.id}>
      <dt><SpeakerIdentity speaker={speaker} person={people.find(person => person.id === speaker.id)} /></dt>
      <dd>{speaker.points} <span>points</span></dd>
    </div>)}</dl>
    <h3>Points by question</h3>
    {scores.questions.map(question => <section key={question.id} className="scoreboard-question" aria-label={question.text}>
      <h4>{question.text}</h4>
      <div className="scoreboard-rows">{question.scores.map(score => {
        const speaker = scores.speakers.find(person => person.id === score.speakerId)!;
        return <div key={score.speakerId} className="scoreboard-row">
          <SpeakerIdentity speaker={speaker} person={people.find(person => person.id === speaker.id)} />
          <strong aria-label={`${speaker.name}: ${score.points} points`}>{score.points}</strong>
          {onEdit && !speaker.removed && <button type="button" aria-label={`Edit points for ${speaker.name}: ${question.text}`}
            onClick={() => onEdit(question, speaker, score.points)}>Edit points</button>}
        </div>;
      })}</div>
      {question.unassignedPoints !== 0 && <p className="fine-print">Unassigned points: {question.unassignedPoints}.
        No published speaker was active for these reactions.</p>}
    </section>)}
    <p className="fine-print">Totals include host adjustments. Individual voters' contribution histories are unchanged.
      Only published speakers and questions appear on the audience scoreboard.</p>
  </div>;
}

function SpeakerIdentity({ speaker, person }: { speaker: SpeakerTotal; person?: Panellist }) {
  return <div className="scoreboard-person">
    {person && <PanellistPhoto person={person} compact />}
    <span>{speaker.name}{speaker.removed ? ' (removed)' : ''}</span>
  </div>;
}
