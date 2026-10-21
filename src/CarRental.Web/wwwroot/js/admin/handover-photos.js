// Photos of the departure and return inspections, reduced in the browser before they are uploaded: at most `maxSide`
// pixels (about 1 600 px, JPEG) plus a small thumbnail, the EXIF orientation applied. It saves mobile data and upload
// time in the car park; the server still checks the real type and the size of what it receives.
const prepared = new Map();

async function decode(file) {
  if (typeof createImageBitmap === "function") {
    try {
      return await createImageBitmap(file, { imageOrientation: "from-image" });
    } catch {
      // Not decodable this way: try an image element below.
    }
  }

  const url = URL.createObjectURL(file);
  const image = new Image();
  image.decoding = "async";
  image.src = url;
  try {
    await image.decode();
  } catch (error) {
    URL.revokeObjectURL(url);
    throw error;
  }

  image.dataset.objectUrl = url;
  return image;
}

function draw(source, maxSide, quality) {
  const width = source.naturalWidth || source.width;
  const height = source.naturalHeight || source.height;
  const scale = Math.min(1, maxSide / Math.max(width, height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(width * scale));
  canvas.height = Math.max(1, Math.round(height * scale));
  const context = canvas.getContext("2d");

  // A transparent picture would turn black in a JPEG.
  context.fillStyle = "#ffffff";
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.drawImage(source, 0, 0, canvas.width, canvas.height);
  return new Promise((resolve, reject) =>
    canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error("The photo could not be encoded."))), "image/jpeg", quality),
  );
}

function newId() {
  return typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

/** Reduces the file chosen in `input` (then empties it, so that the same view can be retaken); null when none. */
export async function prepare(input, maxSide, thumbnailSide) {
  const file = input && input.files && input.files[0];
  if (!file) {
    return null;
  }

  input.value = "";
  let photo = file;
  let thumbnail = null;
  try {
    const source = await decode(file);
    try {
      photo = await draw(source, maxSide, 0.85);
      thumbnail = await draw(source, thumbnailSide, 0.8);
    } finally {
      if (typeof source.close === "function") {
        source.close();
      }

      if (source.dataset && source.dataset.objectUrl) {
        URL.revokeObjectURL(source.dataset.objectUrl);
      }
    }
  } catch {
    // Not an image this browser can decode (e.g. HEIC): sent as it is, the server decides.
    photo = file;
    thumbnail = null;
  }

  const id = newId();
  prepared.set(id, { photo, thumbnail });
  return { id, size: photo.size, hasThumbnail: thumbnail !== null };
}

/** The prepared photo or thumbnail, streamed to .NET. */
export function take(id, part) {
  const entry = prepared.get(id);
  const blob = entry ? entry[part] : null;
  if (!blob) {
    throw new Error("Nothing was prepared.");
  }

  return blob;
}

export function release(id) {
  prepared.delete(id);
}
