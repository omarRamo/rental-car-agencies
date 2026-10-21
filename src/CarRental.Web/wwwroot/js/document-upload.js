// Photos of identity documents (booking funnel step 2 and "Ma réservation", ADR-0050). Progressive enhancement:
// - [data-photos] sections are revealed (inside the drivers form they need this module; a slot that is a form of its
//   own also works without it, as a plain multipart form);
// - a chosen photo is resized in the browser (longest side 1600 px, JPEG) so that it goes through a mobile network,
//   then sent with XMLHttpRequest (progress bar), the antiforgery token of the page and the slot;
// - the server answers with a text only, never with the image or an address of it; the preview is the local file;
// - a failure offers a retry of the same photo.
const MAX_SIDE = 1600;
const QUALITY = 0.85;
const MAX_BYTES = 10 * 1024 * 1024;
const ACCEPTED = ["image/jpeg", "image/png", "application/pdf"];
const TIMEOUT_MS = 120000;

const tokenFor = (element) =>
  (element.closest("form")?.querySelector('input[name="__RequestVerificationToken"]') ??
    document.querySelector('input[name="__RequestVerificationToken"]'))?.value ?? "";

// A JPEG of at most MAX_SIDE pixels, or the file itself (PDF, small images, browsers without canvas support).
async function prepare(file) {
  if (file.type === "application/pdf" || !("createImageBitmap" in window)) {
    return { blob: file, name: file.name || "document" };
  }

  try {
    const bitmap = await createImageBitmap(file, { imageOrientation: "from-image" });
    const scale = Math.min(1, MAX_SIDE / Math.max(bitmap.width, bitmap.height));
    if (scale === 1 && file.type === "image/jpeg" && file.size <= 2 * 1024 * 1024) {
      bitmap.close?.();
      return { blob: file, name: file.name || "photo.jpg" };
    }

    const canvas = document.createElement("canvas");
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    const context = canvas.getContext("2d");
    context.fillStyle = "#ffffff";
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close?.();
    const blob = await new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", QUALITY));
    return blob ? { blob, name: "photo.jpg" } : { blob: file, name: file.name || "photo" };
  } catch {
    return { blob: file, name: file.name || "photo" };
  }
}

function send(url, slot, token, prepared, onProgress) {
  return new Promise((resolve) => {
    const request = new XMLHttpRequest();
    request.open("POST", url);
    request.setRequestHeader("Accept", "application/json");
    request.responseType = "json";
    request.timeout = TIMEOUT_MS;
    request.upload.addEventListener("progress", (event) => {
      if (event.lengthComputable) {
        onProgress(Math.round((event.loaded / event.total) * 100));
      }
    });
    request.addEventListener("load", () => resolve({ status: request.status, body: request.response }));
    request.addEventListener("error", () => resolve({ status: 0, body: null }));
    request.addEventListener("timeout", () => resolve({ status: 0, body: null }));
    const data = new FormData();
    data.append("__RequestVerificationToken", token);
    data.append("slot", slot);
    data.append("file", prepared.blob, prepared.name);
    request.send(data);
  });
}

function setUp(section) {
  section.hidden = false;
  const texts = section.dataset;
  for (const slot of section.querySelectorAll("[data-upload-slot]")) {
    const input = slot.querySelector("[data-upload-input]");
    const status = slot.querySelector("[data-upload-status]");
    const progress = slot.querySelector("[data-upload-progress]");
    const retry = slot.querySelector("[data-upload-retry]");
    const preview = slot.querySelector("[data-upload-preview]");
    const submit = slot.querySelector("[data-upload-submit]");
    const url = slot.dataset.uploadUrl ?? section.dataset.uploadUrl;
    const item = slot.closest(".photo-slot") ?? slot;
    let prepared = null;
    let previewUrl = null;

    if (!input || !status || !url) {
      continue;
    }

    submit?.setAttribute("hidden", "");
    if (slot.tagName === "FORM") {
      slot.addEventListener("submit", (event) => event.preventDefault());
    }

    const upload = async () => {
      retry.hidden = true;
      progress.hidden = false;
      progress.value = 0;
      progress.textContent = "0 %";
      status.textContent = (texts.textSending ?? "").replace("{0}", "0");
      const answer = await send(url, slot.dataset.uploadSlot, tokenFor(slot), prepared, (percent) => {
        progress.value = percent;
        progress.textContent = `${percent} %`;
        status.textContent = (texts.textSending ?? "").replace("{0}", String(percent));
      });
      progress.hidden = true;
      if (answer.status >= 200 && answer.status < 300 && answer.body?.received) {
        status.textContent = answer.body.message ?? texts.textReceived ?? "";
        item.classList.add("photo-slot--received");
        item.classList.remove("photo-slot--failed");
        if (preview && prepared.blob.type.startsWith("image/")) {
          if (previewUrl) {
            URL.revokeObjectURL(previewUrl);
          }

          previewUrl = URL.createObjectURL(prepared.blob);
          preview.src = previewUrl;
          preview.hidden = false;
        }

        return;
      }

      item.classList.add("photo-slot--failed");
      status.textContent = answer.body?.message ?? texts.textFailed ?? "";
      // A refused file (type, size) is not sent again as is: choosing another one is the retry.
      retry.hidden = answer.status === 422 || answer.status === 400;
    };

    input.addEventListener("change", async () => {
      const file = input.files?.[0];
      if (!file) {
        return;
      }

      item.classList.remove("photo-slot--failed");
      if (file.type && !ACCEPTED.includes(file.type) && !file.type.startsWith("image/")) {
        status.textContent = texts.textType ?? "";
        item.classList.add("photo-slot--failed");
        return;
      }

      prepared = await prepare(file);
      if (prepared.blob.size > MAX_BYTES) {
        status.textContent = texts.textTooLarge ?? "";
        item.classList.add("photo-slot--failed");
        return;
      }

      await upload();
    });

    retry?.addEventListener("click", () => {
      if (prepared) {
        upload();
      }
    });
  }
}

for (const section of document.querySelectorAll("[data-photos]")) {
  setUp(section);
}
