# Card fonts

The three faces printed on the card, as static TrueType files (WPF does
not read WOFF2):

| File | Used for | Licence | From |
| --- | --- | --- | --- |
| `Poppins-Bold.ttf` | the name | SIL Open Font License 1.1 | google/fonts `ofl/poppins` |
| `Montserrat-ExtraBold.ttf` | the job title | SIL Open Font License 1.1 | JulietaUla/Montserrat `fonts/ttf` |
| `Arimo-Regular.ttf` | the employee number | Apache License 2.0 | googlefonts/Arimo `fonts/ttf` |

Their advance widths were checked against the WOFF2 subsets the web app
shipped (`assets/fonts/` at the top of the repository) and are identical,
so a name breaks onto the same lines at the same size as before.

They style the printed card only. The window itself uses Segoe UI, which
is already on every Windows computer.
