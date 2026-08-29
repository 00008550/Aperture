# Aperture — the domain, in the business's words

Aperture is the internal platform of a distributor that sells technical equipment to businesses:
drones and cameras, plus accessories, spares, service and warranty work. It replaces a spreadsheet,
a mailbox, and three disconnected tools.

This document is what the business does. `ARCHITECTURE.md` is how the system is built to serve it.
When the two disagree, this one is right and the architecture has a bug.

---

## 1. Who uses it

| Role | What they do | What they must not see |
|---|---|---|
| **Sales agent** | Owns accounts and deals, quotes, converts a deal to an order. | Other agents' deals, unless shared. Cost prices. |
| **Sales lead** | Everything their team owns, reassigns deals, approves discounts. | Other teams' pipelines. |
| **Fulfilment** | Reserves stock, ships, handles backorders and returns. | Deal economics — margin, discount reasoning. |
| **Finance** | Invoices, payments, credit limits. | Nothing much — but every read is audited. |
| **Admin** | Users, roles, integrations. | Nothing. Every action is audited. |
| **Assistant (AI)** | Answers questions and drafts actions on behalf of the signed-in user. | Exactly what that user cannot see. No privileged path. |

**Tenants.** The platform is multi-tenant: the distributor runs its own regional companies on it,
and a tenant's data is never visible to another. This is a hard boundary, not a filter someone can
forget.

## 2. The core objects

```
Account ──< Contact
   │
   └──< Deal ──(won)──> Order ──< OrderLine
                          │
                          └──< Shipment
Account/Deal/Order ──< TimelineEntry   (email, call, note, system event)
```

### Account
A company we sell to. Has a credit limit, payment terms, an owning agent, and a region. Accounts are
**deduplicated on tax identifier** — the same company arriving twice from two sources is one account.

### Contact
A person at an account. May be reachable on several channels (email, phone, messenger). A contact
belongs to exactly one account; a person who moves companies is a new contact, and the old one is
marked as departed rather than deleted, because history must stay attributable.

### Deal
An intent to sell, owned by one agent. Moves through
`new → qualified → quoted → negotiation → won | lost`.

Rules the business actually enforces:

- A deal can only be **won** if it has at least one line with a price and a quantity.
- Moving to **quoted** freezes the price list version used, so a later price change does not silently
  alter an outstanding quote.
- A discount above the agent's threshold requires the lead's approval; the deal stays in
  `negotiation` with a pending approval rather than advancing.
- **Lost** requires a reason code. "No reason" is the most expensive missing field in the old system.

### Order
Created from a won deal, and only from a won deal. Moves through
`draft → confirmed → reserved → picking → shipped → delivered | cancelled | returned`.

- Confirming an order **checks credit**: the account's outstanding balance plus this order must not
  exceed the credit limit, unless finance overrides — the override is recorded with who and why.
- Reservation decrements available stock. Two agents confirming the last unit at the same moment is
  a real, frequent event; exactly one of them gets it and the other is told immediately.
- An order can be **partially shipped**. Backordered lines stay open, and the customer sees one
  order, not two.
- Cancellation after reservation must release the stock. A cancellation that leaks reserved stock is
  how the old system slowly lost inventory accuracy.

### Timeline
Every account, deal and order has one merged, chronological timeline: emails in and out, calls,
notes, and system events ("stock reserved", "credit override by A. Ivanov"). It is the answer to
"what happened with this customer" — one place, not four.

## 3. What comes in from outside

| Source | Direction | Notes |
|---|---|---|
| Supplier catalogue & stock feed | in | Periodic; large; partially wrong. Must be idempotent and must never leave the catalogue half-updated. |
| Email (IMAP/SMTP) | both | Inbound mail is threaded onto the right account by sender address, and onto a deal when it can be resolved. |
| Messenger channel | both | Same timeline, different transport. |
| Accounting system | out | Invoices. It is slow and occasionally returns success twice for one request. |
| Delivery service | both | Tracking numbers in, status webhooks back — **at least once**, out of order, and sometimes for orders we have already closed. |

Every one of these is unreliable in a different way, and each of those ways is a design requirement,
not an operational annoyance.

## 4. What "the AI feature" means here

Not a chatbot bolted to the side. Three concrete jobs:

1. **Ask the platform in words.** "Which deals over 500k are stuck in negotiation more than two
   weeks, mine only?" — answered by calling the same filtered endpoints a human would, under the
   asker's own permissions.
2. **Draft, never send.** A reply to an inbound email, a follow-up note, a lost-reason summary. It
   produces a draft into the timeline; a human sends it.
3. **Explain a record.** "Why is this order stuck?" — reads the order's events and states the actual
   blocking condition, citing the events it used.

Two rules make this safe rather than impressive:

- **The assistant has no privileged data path.** It calls the REST API as the signed-in user. If the
  user cannot see a deal, no prompt makes it visible.
- **Every tool call is audited** like a human action, with the same actor, the same tenant, and a
  marker that it came from the assistant.

## 5. The failures the business has actually had

Listed because they are the acceptance criteria for the rebuild:

1. A report once showed one region's data to another region — a filter that treated "no regions
   selected" as "all regions".
2. Stock drifted from reality over a year because cancelled orders did not always release
   reservations.
3. A supplier feed retried after a timeout and doubled every price import for a day.
4. A delivery webhook arrived twice and moved an order from `delivered` back to `shipped`.
5. Nobody could reconstruct who approved a discount, because approvals were a boolean.

Every invariant in `CLAUDE.md` traces back to one of these.
