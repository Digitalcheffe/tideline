import { describe, expect, it } from 'vitest'
import { addMonths, dateLabel, daysAgoLocal, dollars, monthLabel, monthRange, percent, todayLocal, typeLabel, wholeMonth, wholeYear } from './format'

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

import { parseDollars, ruleWords } from './format'

describe('ruleWords', () => {
  it('keeps the words before the first store number, date or reference', () => {
    expect(ruleWords('KROGER #501 03-22-26 ANYTOWN DEBIT CARD PURCHASE-PIN')).toBe('KROGER')
    expect(ruleWords('ACH PMT SOME-SERVICER 7535 ACH DEBIT')).toBe('ACH PMT SOME-SERVICER')
    expect(ruleWords('AMAZON MKTPL*AB12CDAmzn.com/bill WA')).toBe('AMAZON')
    expect(ruleWords('WALMART.COM 800-925-6278 AR')).toBe('WALMART.COM')
    expect(ruleWords('PAYROLL ACME INC 12AB ACH CREDIT')).toBe('PAYROLL ACME INC')
  })

  it('uses the whole description when nothing useful comes before a number', () => {
    expect(ruleWords('7-ELEVEN 1234 ANYTOWN')).toBe('7-ELEVEN 1234 ANYTOWN')
    expect(ruleWords('INTEREST PAID')).toBe('INTEREST PAID')
    expect(ruleWords('  Check   #2210 ')).toBe('Check #2210')
  })

  it('drops a dangling dash or star and squeezes the spaces', () => {
    expect(ruleWords('DD *   DOORDASH  - 12')).toBe('DD * DOORDASH')
  })
})

describe('parseDollars', () => {
  it('reads plain money amounts to cents', () => {
    expect(parseDollars('10')).toBe(1000)
    expect(parseDollars('10.5')).toBe(1050)
    expect(parseDollars('$1,000.25')).toBe(100025)
    expect(parseDollars('-4')).toBe(-400)
    expect(parseDollars('.5')).toBeNull()
  })
  it('refuses anything else', () => {
    expect(parseDollars('')).toBeNull()
    expect(parseDollars('ten')).toBeNull()
    expect(parseDollars('1.234')).toBeNull()
  })
})

describe('todayLocal', () => {
  it('is the date on the person\'s own clock, even late in the evening when the UTC date is already tomorrow', () => {
    expect(todayLocal(new Date(2026, 9, 25, 23, 59))).toBe('2026-10-25')
    expect(todayLocal(new Date(2026, 0, 1, 0, 1))).toBe('2026-01-01')
  })
  it('counts days back across month and year ends', () => {
    expect(daysAgoLocal(90, new Date(2026, 2, 31, 22, 0))).toBe('2025-12-31')
    expect(daysAgoLocal(0, new Date(2026, 5, 5))).toBe('2026-06-05')
  })
})
