// Byte-level CSV parser for Shadow Dungeon table files (.bin — plain CSV,
// GBK/GB18030-encoded Chinese first column(s), ASCII everywhere else).
//
// Why byte-level: the extracted files contain a mix of raw GBK double-byte
// sequences and U+FFFD replacement runs (EF BF BD) where the extractor already
// lost bytes. Structural characters (comma 0x2C, quote 0x22, CR/LF) are never
// valid GBK trail bytes, so splitting on raw bytes is always safe and keeps
// column alignment perfect even where the Chinese text is corrupted.
// Each field is then decoded individually: pure-ASCII fields verbatim,
// anything with high bytes through iconv-lite GB18030.

import iconv from "iconv-lite";

const COMMA = 0x2c;
const QUOTE = 0x22;
const CR = 0x0d;
const LF = 0x0a;

/** Split a raw buffer into rows of field buffers (RFC-4180-ish quoting). */
export function splitCsvBytes(buf) {
  const rows = [];
  let row = [];
  let field = [];
  let inQuotes = false;
  for (let i = 0; i < buf.length; i++) {
    const b = buf[i];
    if (inQuotes) {
      if (b === QUOTE) {
        if (buf[i + 1] === QUOTE) {
          field.push(QUOTE);
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        field.push(b);
      }
      continue;
    }
    switch (b) {
      case QUOTE:
        inQuotes = true;
        break;
      case COMMA:
        row.push(Buffer.from(field));
        field = [];
        break;
      case CR:
        break;
      case LF:
        row.push(Buffer.from(field));
        field = [];
        rows.push(row);
        row = [];
        break;
      default:
        field.push(b);
    }
  }
  if (field.length || row.length) {
    row.push(Buffer.from(field));
    rows.push(row);
  }
  return rows;
}

/** Decode one field buffer: ASCII fast path, GB18030 for anything else. */
export function decodeField(fieldBuf) {
  let ascii = true;
  for (let i = 0; i < fieldBuf.length; i++) {
    if (fieldBuf[i] >= 0x80) {
      ascii = false;
      break;
    }
  }
  if (ascii) return fieldBuf.toString("ascii");
  return iconv.decode(fieldBuf, "gb18030");
}

const NUM_RE = /^-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/;

/** "12" -> 12, "1.7" -> 1.7, everything else stays a string. */
export function coerce(s) {
  if (s !== "" && NUM_RE.test(s)) return Number(s);
  return s;
}

/**
 * Parse a table file body into { header, rows } of decoded strings.
 * Verifies the table is rectangular (every row has the header's field count).
 */
export function parseTable(buf, fileLabel = "table") {
  const raw = splitCsvBytes(buf);
  if (raw.length === 0) throw new Error(`${fileLabel}: empty file`);
  const decoded = raw.map((r) => r.map(decodeField));
  const width = decoded[0].length;
  for (let i = 0; i < decoded.length; i++) {
    if (decoded[i].length !== width) {
      throw new Error(
        `${fileLabel}: row ${i} has ${decoded[i].length} fields, expected ${width}`
      );
    }
  }
  return { header: decoded[0], rows: decoded.slice(1), width };
}
