import { describe, expect, it } from 'vitest'
import { addMonths, dateLabel, dollars, monthLabel, monthRange, percent, typeLabel, wholeMonth, wholeYear } from './format'

describe('dollars', () => {
  it('formats cents as US dollars with cents', () => {
    expect(dollars(0)).toBe('$0.00')
    expect(dollars(5)).toBe('$0.05')
    expect(dollars(123456789)).toBe('$1,234,567.89')
  })
  it('keeps the minus in front of the dollar sign', () => {
    expect(dollars(-4512)).toBe('-$45.12')
  })
})

describe('dates', () => {
  it('labels months and dates in plain words', () => {
    expect(monthLabel('2026-03')).toBe('Mar 2026')
    expect(dateLabel('2026-03-09')).toBe('Mar 9, 2026')
  })
  it('knows month ranges, including leap years', () => {
    expect(monthRange('2026-03')).toEqual({ from: '2026-03-01', to: '2026-03-31' })
    expect(monthRange('2028-02')).toEqual({ from: '2028-02-01', to: '2028-02-29' })
  })
  it('moves between months across year ends', () => {
    expect(addMonths('2026-01', -1)).toBe('2025-12')
    expect(addMonths('2026-12', 1)).toBe('2027-01')
  })
  it('recognises whole months and years', () => {
    expect(wholeMonth('2026-03-01', '2026-03-31')).toBe('2026-03')
    expect(wholeMonth('2026-03-02', '2026-03-31')).toBeNull()
    expect(wholeYear('2026-01-01', '2026-12-31')).toBe(2026)
    expect(wholeYear('2026-01-01', '2026-06-30')).toBeNull()
  })
})

describe('helpers', () => {
  it('keeps percentages between 0 and 100', () => {
    expect(percent(50, 200)).toBe(25)
    expect(percent(300, 200)).toBe(100)
    expect(percent(-5, 200)).toBe(0)
    expect(percent(5, 0)).toBe(0)
  })
  it('names the line types', () => {
    expect(typeLabel('one_time')).toBe('One-time')
    expect(typeLabel('income')).toBe('In')
    expect(typeLabel(null)).toBe('Move')
  })
})
