# Privacy policy

*DeSign Mage, <https://github.com/dell90wattac/DeSignMage>. Maintainer: [dell90wattac](https://github.com/dell90wattac).*

**This program will not transfer any information to other networked systems unless specifically requested by the user or the person
installing or operating it.**

## In short

* DeSign Mage works completely offline and sends nothing on its own. There are no accounts, no analytics, no tracking and no
  advertising.
* There are two network features. **Feedback / Bug** sends a report only when you press **Send**. A copy installed with the installer
  (Setup) also **looks for new versions** on this project's public GitHub Releases page; the portable zip never does.
* After a crash the program **asks** before it sends anything. "Always send crash reports" is a box you have to tick yourself.
* Your designs, pictures, fonts and settings stay on your computer.

## Updates (installed copy only)

A copy installed with the installer (`DeSignMageApp-win-Setup.exe`) keeps itself up to date, which you chose by installing it this way.
When it starts, and now and then while it is open, it asks **github.com** (this project's public Releases page, over HTTPS) for the list
of versions and downloads a newer one from there. GitHub can see what any download shows: your network address and that this page
was fetched. DeSign Mage adds no name, no settings, no design and no identifier of your computer to these requests. The version you
have is only compared on your computer. Nothing is installed without the "Update required" window you see when you start the program.
The portable zip has no installer and never contacts anything for updates. Uninstalling the program (Windows Settings, Apps) stops it
all; your settings and designs stay where they are.

## Feedback / Bug reports

When you press **Send** in the Feedback / Bug dialog, the program sends:

* what you typed (your description, and the name you entered, if any);
* a log of recent actions in the program and the undo list. These never contain the text or content of your designs;
* basic facts about the program and computer: the program version and build date, the Windows version, the .NET version, the language,
  the number of processors, the amount of memory and the screen sizes;
* **only if you tick the boxes** (both start unticked, and the dialog remembers your last choice): a screenshot of the program window,
  and the open design file.

A report goes over HTTPS to the project's **feedback relay**, a small program running on Cloudflare Workers
(`designmage-feedback.dell100wattac.workers.dev`; its source is public in [`tools/feedback-relay`](tools/feedback-relay)). The relay checks
the report, rebuilds it as a plain message and passes it on to **a private Discord channel** that only the maintainer reads. Your
network address is not passed on to Discord. To limit abuse the relay keeps, for two hours at most, a counter per hashed network address
(not the address itself).

A report that cannot be delivered right away (you are offline, the relay is busy) waits on your computer until it can be sent. You
asked for it by pressing Send, and nothing else is ever queued.

## Crash reports

If the program closes unexpectedly it writes a crash folder on your computer (the error, a log, the system facts above and an emergency
copy of your open design) and **nothing is sent**. The next time the program starts it asks "Send the crash report?":

* **Send** sends the report. It contains the error, the log and the system facts; the design copy is attached **only if** you allowed
  designs in the Feedback dialog.
* **Don't send** keeps everything on your computer and the program does not ask about that crash again.
* Ticking **Always send crash reports, without asking** together with Send makes later crash reports go out without a question. That
  is your explicit choice; it is off until you tick it.

## What is not collected

No accounts or sign-in, no analytics or usage statistics, no tracking, no advertising, no location, no files or folders other than
the ones above and only when you send them. The program does not look at the rest of your computer.

## Where your data is, how long, and how to have it removed

* **On your computer:** settings, logs, unsent reports, crash folders and extra files are in `%LOCALAPPDATA%\DeSignMage`, and the recent
  files list in `%APPDATA%\DeSignMage`. Delete those folders to remove everything. Old crash folders are cleaned up automatically
  after about 60 days.
* **Reports you sent** stay in the maintainer's private Discord channel as long as they are useful for fixing the problem. They are not
  shared with anyone else, not sold and not used for anything else. To have a report removed, open an issue at
  <https://github.com/dell90wattac/DeSignMage/issues> (do not put private details in the issue) and say when you sent it and what name
  you used; the maintainer will delete it.

## Third parties

Reports pass through two services, each under its own privacy policy:

* **Cloudflare**, which runs the relay: <https://www.cloudflare.com/privacypolicy/>
* **Discord**, which holds the reports: <https://discord.com/privacy>

Release downloads are hosted by **GitHub**: <https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>.
Code signing of the release files is provided by SignPath.io and SignPath Foundation; no information about you is involved.

## Changes

If this policy changes, the new version is published in this repository and the change is visible in its history.
