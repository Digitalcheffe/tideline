# Features

A tour of what Tideline does, screen by screen. Pictures are in [screenshots](screenshots/README.md). How it fits together is in
[ARCHITECTURE](ARCHITECTURE.md).

## The ideas it is built on

- **One meaning per line.** Every line of spending is exactly one of **Income, Bill, Required, Extra, One-time or Savings**. That is why the
  month always adds up: In = Bills + Required + Extra + One-time + Savings + Left over.
- **Transfers are not spending.** Money moving between your own accounts is a *move*: two lines (out and in) that are paired and left out of
  the totals. Moves into savings count as Savings.
- **Bank lines are facts.** They are written once and never changed. You change what a line *means*, never its amount or date.
- **Every number is a link** to the lines behind it, with the same filters everywhere.

## Getting your data in

| Way in | What it does |
|---|---|
| **Upload** (Settings, Imports) | Send a Truist CSV export from any browser, including a phone. The account comes from the digits in the file name. |
| **Inbox folder** | Drop files in a folder (a shared folder works) and they are imported every few minutes. A file that fails is moved to a `Failed` folder. |
| **SimpleFIN** (Settings, SimpleFIN) | Pulls new transactions every few hours. A takeover day per account says where files stop and the sync starts. Try a month first compares it with your file. |
| **Add a line** (Accounts) | For cash or anything the bank does not report. |

What happens to every import: lines already there are skipped; each new line goes through your rules; matching transfers are paired; and the
bank's running balance is compared with the ledger. If it does not add up, the file is refused and says why. A failed import can be dismissed
from the list once dealt with. An import that should not have happened (the wrong file or account) can be undone in one step, which leaves all of its
lines out of the totals; Restore puts them back.

## The Ledger

Every line for the period, with filters for account, category, type, direction, tags and text. Open a line to:

- change its category, type, payee, tags and notes (it is then marked as set by you, and rules will not overwrite it);
- **split** it across categories and types without changing the total;
- link it to a **plan** or an **asset**;
- see its raw bank row, which rule set it, and its history;
- **reset it to the rules**, or **create a rule from it**;
- **leave it out of the totals** (a duplicate, or a line that should not count) with a reason, and bring it back any time. The line is not changed or deleted; find them under **Left out**. A line you added by hand can also be deleted.

Tick several lines to change them together, or, with two ticked, to pair them as a transfer.

## Rules

Conditions (words in the description, amount range, account, direction) decide the category, type, payee and tags. They run in order and the first
match wins. Before saving, **See what it would change** lists every past line a draft rule would change. **Apply** then updates past lines, and
skips any you set by hand.

A new household can start from a **starter set** of about 40 categories and 50 rules (`tideline starter`) for national shops, bills and the
wording banks use. They come after any rule you make, so yours always win. Anything they cannot tell goes to Uncategorized with the review tag,
including checks, Zelle payments and small local shops, which a person has to decide.

## Where the money went

- **Overview**: the spread bar and its equation, a monthly chart, the plans and their status, a **Needs you** list, and a **Net worth** tab.
- **Accounts**: each account by month, with in, out and balance.
- **Cards**: was each card paid in full each month (using its statement day if you entered one), interest charged, payments, and spending by category.
- **Bills**: a year grid of recurring bills. Each cell is paid, unusual, extra, due, missing or projected. Future months use the last three payments.
  An **Other** row holds bill lines with no recurring item, so the grid always matches the Overview. Large one-time items are listed for a look.
- **Required** and **Extra**: totals by category and by payee. Extra also lists subscriptions and notices price changes.
- **Income**: expected against actual, by source.

## Looking ahead

- **Budget**: a guideline per category (monthly or yearly), compared with actual spending for the month and the year so far, and a heads-up when a
  category goes well over. **Fill from averages** proposes guidelines from your history.
- **Planning**: plans with a target and a date. Progress comes from an account balance or from lines you link to the plan. Status is on track or
  behind, with the monthly amount that would finish it on time.
- **Savings**: balances, the average monthly deposit, the interest earned, and projections toward each plan.
- **Net worth**: assets and liabilities with a month-end trend. Loans, assets and retirement accounts are entered by figure, each with its date and
  an **old estimate** badge after 90 days.
- **Loans**: payoff month and interest left, and an **extra each month** calculator that shows the months and interest saved.
- **Assets**: worth, owed, equity, **put in** (spending linked to the asset, over its life), the change in value, and the history of figures.
- **Retirement**: people, goal income, assumptions and accounts in; required, projected, the gap, the extra monthly amount to close it, how long
  the money lasts, and when you could retire if nothing changes. Marked clearly as a projection, not advice.

## Alerts, suggestions and notifications

The evaluator runs every night (and on demand) and raises alerts of sixteen kinds. Each shows in **Needs you**, can be acknowledged with a note,
and clears itself when the cause is fixed.

| Alert | When |
|---|---|
| Bill not paid / paid at an odd amount | A recurring item is late, or outside its usual range |
| Card not paid in full / card interest | A card that should be paid in full was not, or interest was charged |
| Balance mismatch / import failed | The ledger disagrees with the bank, or a file or sync failed |
| Transfer unpaired | One side of a transfer has no partner after a week |
| New debt / new recurring charge | A payment looks like a new loan, or a charge repeats and is not yet a recurring item |
| Budget pop / income short | Spending is well over a guideline, or income is behind |
| Plan slipped / plan behind | A saving plan is slipping or off pace |
| Large one-time item / review needed | A big one-time cost, or lines tagged for review |
| Retirement gap grew | The projected gap got bigger |

**Recommendations** shows a plain suggestion for each open alert. You can edit them, switch them off, add your own, and hide one for a single alert.

**Notifications** go to a webhook (for example a flow that posts to chat) or email. Per kind of alert you choose *send now*, *hold for the
monthly summary*, or *off*. Quiet hours hold urgent messages until morning, a flood is collapsed into a digest, failed sends retry, and a
**monthly summary** with the real numbers arrives on the first.

## Security

- **Two steps every time**: a password and a code from an authenticator app, for everyone, with ten single-use recovery codes.
- **Your account page** (click your name): change your name and password, move to a new phone, make new recovery codes, and end sessions on other
  devices.
- **People and roles**: an owner invites people with a one-time setup link. Owner, member and viewer. A viewer can look but not change.
- **Audit log**: every change, with before and after, append-only. Sign-ins and failures too.
- **Privacy by design**: account numbers are never stored, only the last four digits as a keyed hash and a sealed copy. Authenticator secrets,
  webhook and mail settings and the SimpleFIN address are encrypted. Nothing leaves your computer unless you set up a webhook, email or SimpleFIN.
- **API tokens**: read-only by default, for dashboards and automations, shown once and stored as a hash.
- **Backups and export**: a verified copy while the app runs, 30 days kept, and a full CSV export.

## Everyday comfort

- Light, dark, or follow the device. Works on a phone, with a menu that slides out.
- **Help** in the menu: plain-words guides to each part of the app.
- Money is whole cents everywhere, so totals never drift.
