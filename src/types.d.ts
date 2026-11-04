/* ------------------------------------------------------------------
   Shared types for the ID Maker
   ------------------------------------------------------------------
   The app is still a set of classic <script> files sharing one global
   scope (see build.mjs), so these are ambient: no import/export. Every
   measurement is millimetres unless the name says Px.
   ------------------------------------------------------------------ */

/** A point [x, y]. */
type Point = [number, number];
/** A closed outline, as a list of points. */
type Polygon = Point[];

/** An axis-aligned box. A typo here (`{x, y, width}`) used to be silent. */
interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

type TextKey = 'name' | 'title' | 'employeeNo';
type BoxKey = TextKey | 'signature' | 'qr';

/** The front and back artwork, loaded. */
interface Templates {
  front: CanvasImageSource;
  back: CanvasImageSource;
}

/** What `layoutText` returns for one text box. */
interface FittedText {
  lines: string[];
  sizePt: number;
  [k: string]: unknown;
}

/** Things still in plain JavaScript or vendored, seen from the typed files. */
declare const ASSETS: {
  front: string;
  back: string;
  example: string;
  logo: string;
  fonts: Record<string, string[]>;
};

/* ---- config.ts / register.ts / text-fit.ts / titles.ts ---- */
/** Name parts as entered on the form; any may be missing. */
interface NameParts {
  last?: string;
  first?: string;
  middle?: string;
  suffix?: string;
}

/* ---- photo.ts / pdf.ts / qr.ts / batch.ts ------------------------- */

/** vendor/qrcode-generator.js (only what qr.ts uses). */
interface QrCodeGenerator {
  addData(data: string): void;
  make(): void;
  getModuleCount(): number;
  isDark(row: number, col: number): boolean;
}
declare function qrcode(typeNumber: number, errorCorrectionLevel: string): QrCodeGenerator;

/** The cached QR module grid (qr.ts). */
interface QrModules {
  key: string;
  size: number;
  dark: boolean[];
}

/** One encoded page image in the PDF writer; W/H are page size in pt, as text. */
interface PdfImage {
  filter: string;
  bytes: Uint8Array<ArrayBuffer>;
  width: number;
  height: number;
  W?: string;
  H?: string;
}
/** A page with its own size (A4 sheet layout). */
interface PdfSizedPage {
  canvas: HTMLCanvasElement;
  widthMm?: number;
  heightMm?: number;
}
type PdfPage = HTMLCanvasElement | PdfSizedPage;
/** What buildPdf accepts per page: the page, or a thunk that renders it. */
type PdfPageSource = PdfPage | (() => PdfPage | Promise<PdfPage>);

/** One CSV data row; columns come from the header, so any name may appear. */
interface CsvRow {
  _line: number;
  [column: string]: string | number | undefined;
  name?: string; last?: string; first?: string; middle?: string; suffix?: string;
  title?: string; title2?: string; seq?: string; year?: string;
  photo?: string; signature?: string; replacement?: string;
}

/* ---- card.ts / template-map.ts / template-store.ts ----------------- */

/** A photo placed on the card: the image and where it goes (mm). */
interface CardPhoto {
  image: CanvasImageSource;
  placement: Rect & { shortBy: number };
}

/** What drawFront prints for one person. */
interface CardData {
  name: string;
  title: string;
  year: number | string;
  seq: number | string;
  photo?: CardPhoto | null;
  signature?: any;   // drawn by drawSignatureOnCard; owned elsewhere
}

/** The user's manual nudge to an automatic photo placement. */
interface PhotoAdjust { dx: number; dy: number; zoom: number; }

/** Where the head goes (mm), as read from a map. */
interface HeadTarget { top: number; centerX: number; width: number; reachY?: number; }

/** A template map: masks as canvases, boxes in mm, the head target. */
interface TemplateMap {
  masks: Record<string, CanvasImageSource>;
  boxes: Partial<Record<BoxKey, Rect>>;
  head: HeadTarget | null;
  photoBottom?: number;
  width?: number;
  height?: number;
  found?: string[];
}

/** The geometry the card is drawn with (CONFIG, or a map's answers). */
interface TemplateGeometry {
  text: typeof CONFIG.text;
  photo: typeof CONFIG.photo;
  qr: typeof CONFIG.qr;
  signature: typeof CONFIG.signature;
  masks: Record<string, CanvasImageSource> | null;
}

/** An uploaded or edited design, as kept in localStorage. */
interface CustomTemplate {
  name: string;
  front: string | null;   // data URL, or null for the built-in artwork
  back: string | null;
  boxes: Record<BoxKey, Rect>;
  photoRect: Rect;
}

/** planSheet's answer. */
interface SheetPlan {
  paperName: string;
  mode: string;
  turned: boolean;
  landscape: boolean;
  cols: number;
  rows: number;
  count: number;
  waste: number;
  pw: number;
  ph: number;
  cardW: number;
  cardH: number;
  cellW: number;
  cellH: number;
  left?: number;
  top?: number;
}

interface SheetCell { col: number; row: number; x: number; y: number; }

/** A PDF page, rendered on demand, with its size in mm. */
type PageThunk = (() => HTMLCanvasElement) & { widthMm?: number; heightMm?: number };
