# Phone layout, first round (#100)

Status: design agreed in conversation 2026-10-26. **Build is on hold** by the owner's choice; this document is what to plan from when the work is picked up.

## Intent

Marquee has only ever been designed for desktop. The moment that matters on a phone: someone opens Marquee, claps on a live Premiere, sees the reveal, then goes to their Library to find the ticket. It should feel as live and responsive as desktop, and be fully functional. It is not a cut-down "mobile version".

This is a responsive web layout. It is not a native app (CLAUDE.md section 6), and it does not add push or platform notifications (also section 6). How a person learns a Premiere opened is out of scope; this is about the page working once they arrive.

## Evidence

Measured 2026-09-29 (see #100 for the table): every signed-in page scrolls sideways at 390px because the top nav neither fits nor wraps. `/premiere` is 941px wide because `.body-row` (`premiere.component.css`) puts a fixed 268 x 402 poster stage and the progress/lobby/clap column side by side at every width, pushing the count, lobby, pips and Clap button outside the card. Sign-in pages are slightly wider than the viewport. Library was measured only with empty data.

## Goals

- No horizontal overflow on the in-scope routes at 360, 390 and 430px, signed in and signed out, with realistic data.
- Clapping is always one thumb-tap away on an active Premiere.
- The Library keeps everything it does on desktop: browse, sort and filter.
- The desktop layout is unchanged.

## Non-goals

- Friends, profile and admin pages. These are a **second pass** under the same issue (#100). The profile link and sign out still need a home in the new nav, but the pages themselves are not reworked here.
- Native apps, push or platform notifications, any change to clap behaviour (the optimistic count and paced queue from #136 stay exactly as they are).
- Fixing the clap pips overflow at a large cap on desktop (#96). When both are done, check the pips at phone widths too.
- Sub-11px text sweep (parked separately).

## Anti-goals

- A phone layout that changes desktop pixels.
- A pinned Clap bar that hides the pips, the count or the reveal.
- Behavioural drift between desktop and phone (same data, same live updates, same states).

## Decisions

Made by the owner, with mockups:

1. **Nav: bottom tab bar.** Tabs for Premiere, Library and Friends. The top bar shrinks to the logo plus the user's face. Not a hamburger menu, not a two-row top bar.
2. **Premiere: clap pinned.** Status and timer, the sign, the poster scaled to the screen width, the count, progress bar and lobby faces scroll. The user's pips and the Clap button stay pinned just above the tab bar.
3. **Library: the normal page, fitted to the phone.** All current features, same behaviour.
4. **Scope of the first round:** the nav and top bar, the Premiere in all its states, the Library, and the sign-in, confirm-email and reset-password pages (you cannot reach anything without them).
5. **Phone layout applies below about 640px**; above that the desktop layout is untouched. The 640px figure comes from #100's own suggestion and was not contested.

## Builder's calls (the owner has not decided these; change freely)

- **Face menu holds the rest.** Tapping the face opens profile and sign out. Operations (admin) is reachable only from this menu on a phone, since admin is second-pass.
- **Visitors.** A signed-out visitor sees the Premiere tab plus Sign in and Create account; Library and Friends stay as locked, non-link tabs, mirroring the desktop treatment.
- **The pinned dock follows the Premiere's state.** Active: pips and Clap. Opened: the dock becomes the reveal's "See your library". Visitor on an active Premiere: "Log in to clap". No active Premiere: no dock, and the programme ledger and last night's showing stack in the page.
- **Library controls.** The filter row collapses into something tighter than the desktop row and the poster grid reflows to fewer columns. Exact pattern is left to the builder, to be designed against the real page.
- **Delivery order, as separate PRs:** nav and top bar, then Premiere, then Library, then the auth pages.
- **Overflow check.** A committed, rerunnable Playwright script that visits each in-scope route at 360, 390 and 430px, signed in and out, and asserts `document.documentElement.scrollWidth <= innerWidth`. The issue's done-when asks for a scripted check; committing it keeps the second pass honest.

## Constraints

- Angular standalone components with signals, existing design tokens ("Neon & chrome, 1958", #32). Use the existing CSS custom properties; no new palette.
- Component CSS budget: `premiere.component.css` is already close to its `anyComponentStyle` limit (16 kB, split tracked in #101). The Premiere work will probably need #101 first, or to split as it goes.
- `library.component.css` is already over budget by about 600 bytes in the current build; fixing that is part of touching it.
- Frontend files use CRLF. Sentence case in UI copy.

## Open questions for the builder

- Where exactly the 640px breakpoint should sit relative to tablets (the owner only asked for phones).
- Whether the TMDB attribution footer needs to move to make room for the tab bar (it renders on every route, #150).
- Safe-area insets (notched phones) for the bottom bar and the pinned dock.

## Tests

The Playwright overflow check above, plus Karma specs for any new component logic (face menu open/close, dock state per Premiere state). Desktop layout unchanged is checked visually at 1280px against main before each PR.

## Next step

When the owner wants to start: run `superpowers:writing-plans` on this document, then build in the order above.
