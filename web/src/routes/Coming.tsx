import { Card } from '../components'

export function Coming({ title, phase }: { title: string; phase: string }) {
  return (
    <>
      <h1>{title}</h1>
      <Card>{phase ? <p className="muted">This screen arrives in {phase}.</p> : <p className="muted">There is nothing at this address.</p>}</Card>
    </>
  )
}
