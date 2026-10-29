import { useQuery } from '@tanstack/react-query'
import { get, type NeedsCategory } from './api'

/** How many lines are waiting for a category, now. Everything that changes a line refreshes every query, so this stays current. */
export function useNeedsCategory() {
  return useQuery({ queryKey: ['needs-category'], queryFn: () => get<NeedsCategory>('/needs-category'), retry: false })
}
