// PDF Utility Functions — On-device PDF merge + page extraction using pdf-lib
// No native modules needed — pure JavaScript

import { PDFDocument } from 'pdf-lib';
import * as FileSystem from 'expo-file-system';

/**
 * Read a PDF file from a URI (local or http) and return its bytes as Uint8Array.
 */
async function readPdfBytes(uri: string): Promise<Uint8Array> {
  if (uri.startsWith('http://') || uri.startsWith('https://')) {
    // Remote file: download to temp location first
    const tempPath = `${FileSystem.cacheDirectory}pdf_temp_${Date.now()}_${Math.random().toString(36).slice(2)}.pdf`;
    const { uri: localUri } = await FileSystem.downloadAsync(uri, tempPath, {
      headers: { 'X-Advance-Client': 'MobileCompanion' },
    });
    const base64 = await FileSystem.readAsStringAsync(localUri, { encoding: FileSystem.EncodingType.Base64 });
    await FileSystem.deleteAsync(localUri, { idempotent: true });
    return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
  } else {
    // Local file
    const fileUri = uri.startsWith('file://') ? uri : `file://${uri}`;
    const base64 = await FileSystem.readAsStringAsync(fileUri, { encoding: FileSystem.EncodingType.Base64 });
    return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
  }
}

/**
 * Merge multiple PDF files into a single PDF.
 * @param pdfUris - Array of PDF file URIs (local paths or HTTP URLs)
 * @param outputPath - Where to save the merged PDF
 * @returns The output file path
 */
export async function mergePdfs(pdfUris: string[], outputPath: string): Promise<string> {
  const mergedPdf = await PDFDocument.create();

  for (const uri of pdfUris) {
    try {
      const pdfBytes = await readPdfBytes(uri);
      const sourcePdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
      const pageIndices = sourcePdf.getPageIndices();
      const copiedPages = await mergedPdf.copyPages(sourcePdf, pageIndices);
      copiedPages.forEach(page => mergedPdf.addPage(page));
    } catch (err: any) {
      console.warn(`Failed to load PDF from ${uri}: ${err.message}`);
      throw new Error(`Failed to load PDF: ${uri.split('/').pop()}`);
    }
  }

  const mergedBytes = await mergedPdf.save();
  const base64 = uint8ArrayToBase64(mergedBytes);
  
  // Ensure output directory exists
  const dir = outputPath.substring(0, outputPath.lastIndexOf('/'));
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});
  
  await FileSystem.writeAsStringAsync(outputPath, base64, { encoding: FileSystem.EncodingType.Base64 });
  return outputPath;
}

/**
 * Extract specific pages from a PDF and save as a new PDF.
 * @param pdfUri - Source PDF file URI
 * @param pageNumbers - 1-indexed page numbers to extract (in desired order)
 * @param outputPath - Where to save the extracted PDF
 * @returns The output file path
 */
export async function extractPages(pdfUri: string, pageNumbers: number[], outputPath: string): Promise<string> {
  const pdfBytes = await readPdfBytes(pdfUri);
  const sourcePdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
  const newPdf = await PDFDocument.create();

  // Convert 1-indexed page numbers to 0-indexed
  const pageIndices = pageNumbers.map(n => n - 1).filter(i => i >= 0 && i < sourcePdf.getPageCount());

  if (pageIndices.length === 0) {
    throw new Error('No valid pages selected');
  }

  const copiedPages = await newPdf.copyPages(sourcePdf, pageIndices);
  copiedPages.forEach(page => newPdf.addPage(page));

  const newBytes = await newPdf.save();
  const base64 = uint8ArrayToBase64(newBytes);

  const dir = outputPath.substring(0, outputPath.lastIndexOf('/'));
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});

  await FileSystem.writeAsStringAsync(outputPath, base64, { encoding: FileSystem.EncodingType.Base64 });
  return outputPath;
}

/**
 * Get the page count of a PDF file.
 */
export async function getPdfPageCount(pdfUri: string): Promise<number> {
  const pdfBytes = await readPdfBytes(pdfUri);
  const pdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
  return pdf.getPageCount();
}

/**
 * Get page dimensions for all pages in a PDF.
 * Returns array of { width, height } for each page.
 */
export async function getPdfPageInfo(pdfUri: string): Promise<{ pageCount: number; pages: { width: number; height: number }[] }> {
  const pdfBytes = await readPdfBytes(pdfUri);
  const pdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
  const pages = pdf.getPages().map(page => {
    const { width, height } = page.getSize();
    return { width, height };
  });
  return { pageCount: pdf.getPageCount(), pages };
}

/** Convert Uint8Array to base64 string */
function uint8ArrayToBase64(uint8Array: Uint8Array): string {
  let binary = '';
  const chunkSize = 8192;
  for (let i = 0; i < uint8Array.length; i += chunkSize) {
    const chunk = uint8Array.slice(i, i + chunkSize);
    binary += String.fromCharCode(...chunk);
  }
  return btoa(binary);
}
