import { Lines } from './Lines'

/** The pile of lines the rules could not place. It is the Ledger, showing only those lines, with a quick answer on each row. */
export function Needs() {
  return <Lines needsMode />
}
