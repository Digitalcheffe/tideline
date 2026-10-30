import type { ReactNode } from 'react'
import { Link, NavLink, Navigate, useParams } from 'react-router-dom'
import { Card } from '../components'

interface Topic {
  id: string
  title: string
  body: ReactNode
}

const L = ({ to, children }: { to: string; children: ReactNode }) => <Link to={to}>{children}</Link>

const TOPICS: Topic[] = [
  {
    id: 'start',
    title: 'Start here',
    body: (
      <>
        <p>
          Tideline is your household ledger. You give it your bank exports, it sorts every transaction, and it shows where the money went each month: what you
          had coming in, what the bills were, what you had to spend, what you chose to spend, what you saved, and what was left.
        </p>
        <h3>The first hour</h3>
        <ol>
          <li>
            <strong>Add your accounts</strong> in <L to="/settings/accounts">Settings, Money accounts</L>: checking, savings and cards. Loans, houses and retirement accounts go there too.
          </li>
          <li>
            <strong>Add your categories</strong> in <L to="/settings/categories">Settings, Categories and tags</L>, such as Groceries or Electric. Each category has a usual type.
          </li>
          <li>
            <strong>Import a bank export</strong> from <L to="/settings/imports">Settings, Imports</L>. Start with one month of one account.
          </li>
          <li>
            <strong>Look at what came in.</strong> Lines the rules could not place wait in <L to="/needs">Needs a category</L>, which also shows as a count on the Overview and in the menu. Pick what each one was and press <strong>Answer</strong>;
            tick <strong>Remember as a rule</strong> to teach the rules so the next import is better. A check is only a number from the bank, so each one gets its own alert asking what it was.
          </li>
          <li>
            <strong>Read the screens.</strong> <L to="/">Overview</L> is the summary. Everything else is a closer look.
          </li>
        </ol>
        <h3>The one idea to know</h3>
        <p>
          Every line of spending has exactly one <strong>type</strong>: Income, Bill, Required, Extra, One-time or Savings. That is what makes the numbers add up: In equals Bills plus Required
          plus Extra plus One-time plus Savings plus what is left over. Money moving between your own accounts is a <strong>transfer</strong>, not spending, and is left out.
        </p>
        <p>
          Every number on every screen is a link. Click it and you see the exact lines behind it.
        </p>
      </>
    ),
  },
  {
    id: 'import',
    title: 'Importing bank files',
    body: (
      <>
        <p>
          Tideline reads the CSV export from your bank (the Truist formats today). You can send files two ways, and both do the same work.
        </p>
        <h3>Upload</h3>
        <p>
          In <L to="/settings/imports">Settings, Imports</L>, choose a file. The account is found from the digits in the file name, such as <code>acct_1111_03_01_2026_to_03_31_2026.csv</code>,
          matched to the last four digits you gave that account. Files can be up to 5 MB.
        </p>
        <h3>The inbox folder</h3>
        <p>
          If the app is set up with an inbox folder, drop files in it and they are picked up every few minutes. A file that fails is moved to a <code>Failed</code> folder inside it.
        </p>
        <h3>What happens to a file</h3>
        <ul>
          <li>Lines already imported are skipped, so overlapping files are safe.</li>
          <li>Each new line goes through your rules to get a category, a type and tags.</li>
          <li>Matching lines on two of your accounts are paired as a transfer.</li>
          <li>The bank's running balance is checked against the ledger. If they disagree, the file is refused and says why.</li>
        </ul>
        <h3>If something goes wrong</h3>
        <p>
          A failed import shows in <strong>Recent imports</strong> with the reason. Fix the cause (usually the wrong account number in the file name, or a missing file for earlier days) and import again.
          Once you have dealt with it, press <strong>Dismiss</strong> to clear it from the list and stop the alert. The record stays in the audit log.
        </p>
        <p>
          Bank lines can never be edited or deleted. You change what they <em>mean</em> (category, type, tags, notes, splits), never the amount or date.
        </p>
        <h3>If something should not be counted</h3>
        <p>
          A duplicate, or a line that should never have been counted: open it in the <L to="/lines">Ledger</L> and press <strong>Leave out of the totals</strong> (you can give a reason).
          Nothing is deleted. The line stays as the bank gave it, but is in no total, balance or alert. Tick <strong>Left out</strong> in the Ledger to see them, and <strong>Bring it back</strong> any time.
          Several lines can be ticked and left out together. A line that is part of a transfer has to be unpaired first.
        </p>
        <p>
          A whole file that went in wrong (the wrong account, or twice): in <strong>Recent imports</strong> press <strong>Undo this import</strong>. Every line of it leaves the totals in one step, and <strong>Restore</strong> puts them back.
        </p>
      </>
    ),
  },
  {
    id: 'ledger',
    title: 'The Ledger and fixing lines',
    body: (
      <>
        <p>
          The <L to="/lines">Ledger</L> lists every line for the period chosen in the menu. Use the filters at the top (account, category, type, direction, tags) or click anything in a line to filter by it.
        </p>
        <h3>Open a line</h3>
        <p>Click a line to see the drawer. The top part is what the bank said and never changes. Below it you can:</p>
        <ul>
          <li>change the <strong>category</strong>, <strong>type</strong>, payee, tags and notes;</li>
          <li><strong>split</strong> it, for example a store trip that was part groceries and part something else. The total never changes;</li>
          <li>link it to a <strong>plan</strong> (a saving goal) or an <strong>asset</strong> (a repair on the house);</li>
          <li>press <strong>Reset to rules</strong> to let the rules decide again;</li>
          <li>press <strong>Create rule from this</strong> to teach Tideline so you never fix it again.</li>
        </ul>
        <h3>Many at once</h3>
        <p>
          Tick the boxes on several lines. A bar appears to set the category, type, asset or tags on all of them, or, with exactly two ticked, to <strong>Pair as a transfer</strong>.
        </p>
        <h3>Transfers</h3>
        <p>
          A transfer between your own accounts has two lines, one out and one in, for the same amount. Tideline pairs them by itself when it can. If it cannot, pair them by hand as above. If one was
          paired wrongly, open it and press <strong>Not a transfer: unpair</strong>.
        </p>
        <h3>Adding a line by hand</h3>
        <p>
          On <L to="/accounts">Accounts</L>, each account has an <strong>Add a line</strong> button, for cash or anything the bank file does not have. A hand-added line can be deleted from its drawer, or just left out of the totals.
        </p>
      </>
    ),
  },
  {
    id: 'rules',
    title: 'Rules',
    body: (
      <>
        <p>
          Rules decide a line's category, type and tags when it comes in. They live in <L to="/settings/rules">Settings, Rules</L>.
        </p>
        <ul>
          <li>A rule has <strong>conditions</strong> (the description contains some words, the amount is in a range, the account is a certain one) and what to <strong>set</strong> when they all match.</li>
          <li>Rules are tried <strong>in order from the top</strong>, and the first match wins. Put specific rules above general ones. Use the arrows to move them.</li>
          <li>
            A last catch-all rule sends anything unmatched to Uncategorized with the <em>review</em> tag. Money coming into a checking or savings account counts as Income until you decide; anything else counts
            as Extra.
          </li>
          <li>
            A new Tideline can start with a ready-made set of categories and rules. They are called <em>Starter: ...</em> and come after your own rules, so yours win. An owner adds them with the
            <strong> Add the starter set</strong> button at the top of this page, best before the first import. It shows only while something is still missing. You can change or switch off any of them.
          </li>
        </ul>
        <h3>Try before you save</h3>
        <p>
          <strong>See what it would change</strong> runs a draft rule over a past period and lists every line whose meaning would change, before anything is saved. <strong>Apply</strong> then changes past lines, and
          skips any you set by hand.
        </p>
        <p>The quick way to make a rule is from a line: open it in the Ledger and press <strong>Create rule from this</strong>.</p>
      </>
    ),
  },
  {
    id: 'screens',
    title: 'Reading the screens',
    body: (
      <>
        <p>The period picker at the top of the menu (month, year or custom range) sets the dates for most screens and is kept as you move between them.</p>
        <ul>
          <li><L to="/">Overview</L>: the month at a glance, your plans, and what needs you. The <strong>Net worth</strong> tab shows what you own and owe.</li>
          <li><L to="/accounts">Accounts</L>: each account by month, with money in, money out and the balance at month end.</li>
          <li><L to="/cards">Cards</L>: each credit card, whether it was paid in full each month, and what was bought.</li>
          <li><L to="/bills">Bills</L>: a year grid, one row per recurring bill, one column per month.</li>
          <li><L to="/required">Required</L> and <L to="/extra">Extra</L>: the totals by category and who you paid. Extra also lists subscriptions.</li>
          <li><L to="/income">Income</L>: what you expected against what came in.</li>
        </ul>
        <h3>The Bills grid</h3>
        <p>
          Each cell has a status in words: <strong>paid</strong>; <strong>unusual</strong> (paid, but for an amount outside the usual range); <strong>extra</strong> (paid in a month it was not expected, such as a
          yearly bill); <strong>due</strong> (it is the month, not paid yet, and still within the grace days); <strong>missing</strong> (late); or <strong>projected</strong> (a future month, using the usual amount). Months
          with nothing expected, for bills that come every other month or once a year, stay blank. Bill lines that belong to no recurring item are gathered in an <strong>Other</strong> row so the grid always adds up to the
          Bills number on the Overview.
        </p>
        <p>
          <strong>Find my bills.</strong> <L to="/settings/recurring">Settings, Recurring</L> lists the bills and subscriptions Tideline found in lines that are not tracked yet: payments that repeat every month, every
          other month, every three or six months, or once a year, for about the same amount. Untick any you do not want, rename any, and press the button. Each becomes a recurring item, its past lines are linked, and next
          month's link themselves. You can also add one by hand there, with the usual amount range and the day it is due.
        </p>
      </>
    ),
  },
  {
    id: 'plans',
    title: 'Budget, plans and savings',
    body: (
      <>
        <h3>Budget</h3>
        <p>
          On <L to="/budget">Budget</L>, set a guideline for each category, monthly or yearly. The screen shows each guideline against what you really spent, for the month and for the year so far.
          <strong> Fill from averages</strong> proposes guidelines from your own history; it only suggests, and you choose what to keep. When a category goes well over its guideline you get a heads up.
        </p>
        <h3>Plans</h3>
        <p>
          A plan is something you are saving toward: a furnace, a trip. On <L to="/planning">Planning</L>, give it a target amount and a date. Progress is counted either from an
          account balance (a savings account set aside for it) or from lines you link to the plan. The plan shows whether you are <strong>on track</strong> or <strong>behind</strong>, and how much a month would finish it
          on time.
        </p>
        <h3>Savings</h3>
        <p>
          <L to="/savings">Savings</L> shows your savings accounts, what goes into each month, the interest they pay (if you entered the rate in Money accounts), and the plans they fund.
        </p>
      </>
    ),
  },
  {
    id: 'worth',
    title: 'Net worth, loans, assets and retirement',
    body: (
      <>
        <p>
          Money accounts get their balance from the ledger. Loans, houses, cars and retirement accounts have no lines, so you type in their value now and then. Each figure shows the day it is
          as of, and an <strong>old estimate</strong> badge appears when it is over 90 days old.
        </p>
        <ol>
          <li>Add the account in <L to="/settings/accounts">Settings, Money accounts</L> with its kind (loan, asset or retirement). For a loan, add its rate and monthly payment, and the asset it is for.</li>
          <li>Enter its current value or balance, with a date. On the Overview, the Net worth tab has the buttons.</li>
        </ol>
        <h3>Loans</h3>
        <p>
          With a balance, a rate and a payment, Tideline shows the payoff month and the interest left. Try an extra amount each month and it shows how many months and how much interest that saves.
        </p>
        <h3>Assets</h3>
        <p>
          <L to="/assets">Assets</L> shows what each house or car is worth, what is owed on it, and the equity. Link lines to an asset (open the line, choose the asset) and you also see what you have put into it
          over its life, set against how its value changed.
        </p>
        <h3>Retirement</h3>
        <p>
          <L to="/retirement">Retirement</L> needs the people (birth date, retirement age, Social Security estimate), a yearly income goal in today's dollars, and your retirement accounts. It then shows what you will
          need, what you are on track for, the gap, and the extra per month that would close it. It is a projection to think with, not advice, and every assumption can be changed.
        </p>
      </>
    ),
  },
  {
    id: 'alerts',
    title: 'Alerts, suggestions and notifications',
    body: (
      <>
        <p>
          Tideline watches for things that need a person: a bill that has not been paid, a credit card balance carried over, a new loan payment, a budget that popped, a plan that is behind. Each shows in
          <strong> Needs you</strong> on the Overview.
        </p>
        <ul>
          <li><strong>Yes, I know about it</strong> acknowledges an alert, with an optional note. It stays on record but stops asking.</li>
          <li>An alert clears by itself when the cause is fixed, for example when the late bill is paid.</li>
        </ul>
        <h3>Recommendations</h3>
        <p>
          <L to="/recommendations">Recommendations</L> shows things worth a look, such as what to check when a bill is late, a large tax refund or payment, nothing going into savings,
          or too many subscriptions. They are prompts to look, not advice. Press <strong>Reviewed</strong> when you have looked; it comes back when it next applies. Every suggestion has an on switch, and an
          owner can choose which ones also send a message. The suggestions come with the app, and their words are not editable.
        </p>
        <h3>Notifications</h3>
        <p>
          In <L to="/settings/notifications">Settings, Notifications</L>, add a channel (a webhook address, such as an automation or chat tool, or email). For each kind of alert choose whether it is sent <strong>now</strong>,
          held for the <strong>monthly summary</strong>, or <strong>off</strong>. Quiet hours hold urgent messages until morning. The monthly summary is sent on the first of the month, and its text can be previewed before it goes out.
        </p>
      </>
    ),
  },
  {
    id: 'simplefin',
    title: 'Automatic bank sync (SimpleFIN)',
    body: (
      <>
        <p>
          Instead of uploading files, Tideline can fetch new transactions every few hours through <strong>SimpleFIN Bridge</strong>, a separate service that connects to your bank. It is set up on
          <strong> <L to="/settings/simplefin">Settings, SimpleFIN</L></strong>, where the steps are written out.
        </p>
        <ul>
          <li>You make a one-time <strong>setup token</strong> at SimpleFIN and paste it in. It works once.</li>
          <li>You match each SimpleFIN account to one of yours and choose the <strong>takeover day</strong>: files cover the days before it, the sync covers the days from it.</li>
          <li>Before switching over, <strong>try a month first</strong>: it compares what SimpleFIN says with the file for the same dates and lists any difference.</li>
          <li>The sync is limited to a few requests a day, so it can never run away.</li>
        </ul>
        <p>
          <strong>Disconnect</strong> only makes Tideline forget the connection. To stop access for good, also revoke it at SimpleFIN Bridge.
        </p>
      </>
    ),
  },
  {
    id: 'account',
    title: 'Your account and sign-in',
    body: (
      <>
        <p>
          Click your name at the bottom left of the menu to open <L to="/account">Your account</L>. Signing in always takes your password and then a six-digit code from an authenticator app. That second step cannot be
          turned off.
        </p>
        <ul>
          <li><strong>Name and password:</strong> change either. A new password signs you out on your other devices.</li>
          <li><strong>New phone:</strong> prove it is you with a code from the old one (or a recovery code if it is gone), scan the new code, and the old phone stops working.</li>
          <li><strong>Recovery codes:</strong> ten single-use codes for the day you lose your phone. They are shown once, so keep them in a password manager. You can make new ones any time.</li>
          <li><strong>Where you are signed in:</strong> every device, with a button to sign any of them out.</li>
        </ul>
        <h3>If you are locked out</h3>
        <p>
          Five wrong tries lock the account for 15 minutes. If you lose both the phone and the recovery codes, an owner can reset your second step from <L to="/settings/people">Settings, People</L> (they get a new setup link for you).
          If you are the only owner, run <code>tideline user reset-mfa --email you@example.com</code> on the server.
        </p>
        <h3>Adding people</h3>
        <p>
          An owner invites someone in <L to="/settings/people">Settings, People</L>: it gives a one-time link that lasts 15 minutes, where the person chooses a password and sets up their authenticator. If it runs out, make another; the new one cancels the old.
          Roles: <strong>owner</strong> can do everything, <strong>member</strong> can change lines and plans, <strong>viewer</strong> can only look.
        </p>
      </>
    ),
  },
  {
    id: 'data',
    title: 'Backups and your data',
    body: (
      <>
        <p>
          Your data is one database file on your own computer. Nothing is sent anywhere unless you set up a webhook, email or SimpleFIN.
        </p>
        <ul>
          <li><strong>Back up now</strong> in <L to="/settings/data">Settings, Data</L> makes a safe copy while the app runs, checks it opens and adds up, and keeps 30 days of copies.</li>
          <li>Backups also copy the <strong>keys</strong> that protect secrets such as authenticator codes. <strong>Without the keys, a restored database cannot be signed in to.</strong> Keep both together, somewhere safe.</li>
          <li><strong>Export everything</strong> downloads every table as a CSV in one zip, to keep or to move elsewhere.</li>
          <li>Restoring is done from the command line: <code>tideline restore &lt;file&gt;</code> (see the project docs).</li>
        </ul>
        <p>
          The <strong>Audit log</strong> in <L to="/settings/logs">Settings, Logs</L> records every change anyone makes, with before and after, and cannot be edited.
        </p>
      </>
    ),
  },
  {
    id: 'faq',
    title: 'Questions and answers',
    body: (
      <>
        <h3>The ledger does not match my bank</h3>
        <p>
          <L to="/settings/imports">Settings, Imports</L> compares the ledger to the bank's last balance for each account. A missing file or a missing day is the usual cause. Import the file for the days involved. A line you added by hand
          also moves the balance, so it will show a difference until the bank file covers it.
        </p>
        <h3>A bill says missing but I paid it</h3>
        <p>
          The payment line has to be linked to the recurring item. Open the line in the Ledger and set the recurring item, or make a rule so the next one links by itself. Also check the amount is inside the usual range.
        </p>
        <h3>A transfer is counted as spending</h3>
        <p>It has not been paired. Select both lines in the Ledger and press <strong>Pair as a transfer</strong>.</p>
        <h3>What does the review tag mean?</h3>
        <p>
          No rule matched, so the line went to Uncategorized and was tagged for you to look at. Money coming into a checking or savings account is counted as Income until you decide, so a first paycheck is not
          shown as spending. Answer it in <L to="/needs">Needs a category</L> (or change it in the Ledger), and tick <strong>Remember as a rule</strong> if it will happen again. Once it has a real category the review tag
          goes, and it leaves the count.
        </p>
        <h3>A check keeps asking what it was</h3>
        <p>
          The bank gives only a number and an amount for a check, never who it was written to, so Tideline asks about each one until it has a real category. If you write a check for the same amount every time, such as
          rent or a sitter, make a rule that starts with <em>Check #</em> and has that amount, and the next one sorts itself.
        </p>
        <h3>I imported the same file twice</h3>
        <p>Nothing doubles. Lines already in the ledger are skipped.</p>
        <h3>Can I change an amount or a date?</h3>
        <p>
          Not on a bank line, on purpose, so the ledger always agrees with the bank. Change the category, type, splits or tags, or add a line by hand to correct something.
        </p>
        <h3>Where do I turn the colours dark?</h3>
        <p>The Auto, Light and Dark switch at the bottom of the menu. Auto follows your device.</p>
      </>
    ),
  },
]

/** How to use Tideline, written for the person using it. Plain words, one topic at a time. */
export function Help() {
  const { topic } = useParams()
  const current = TOPICS.find((t) => t.id === topic)
  if (!current) return <Navigate to={`/help/${TOPICS[0].id}`} replace />

  return (
    <>
      <h1>Help</h1>
      <nav className="subnav" aria-label="Help topics">
        {TOPICS.map((t) => (
          <NavLink key={t.id} to={`/help/${t.id}`} className={({ isActive }) => (isActive ? 'active' : '')}>
            {t.title}
          </NavLink>
        ))}
      </nav>
      <Card title={current.title} className="help">
        {current.body}
      </Card>
    </>
  )
}
