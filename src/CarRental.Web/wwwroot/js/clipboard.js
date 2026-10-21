// Copies a text to the clipboard (back office: payment links). Resolves to false when the browser refuses.
export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
