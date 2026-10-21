// A signature drawn with a finger, a stylus or a mouse on a canvas (pointer events). The canvas follows its displayed
// size at the pixel ratio of the screen (a drawing is kept, scaled, when it changes); `toPng` exports the drawing on a
// white background. The accessible alternative (signature on paper, with its reason) is offered by the page: this pad
// is for pointers only.
export function attach(canvas, dotnet) {
  const context = canvas.getContext("2d");
  let drawing = false;
  let empty = true;
  let last = null;

  const ratio = () => window.devicePixelRatio || 1;

  const changed = (signed) => {
    dotnet.invokeMethodAsync("OnSignatureChanged", signed).catch((error) => console.error("signature-pad", error));
  };

  const style = () => {
    context.setTransform(ratio(), 0, 0, ratio(), 0, 0);
    context.lineWidth = 2.5;
    context.lineCap = "round";
    context.lineJoin = "round";
    context.strokeStyle = "#111827";
    context.fillStyle = "#111827";
  };

  const resize = () => {
    const rect = canvas.getBoundingClientRect();
    const width = Math.max(1, Math.round(rect.width * ratio()));
    const height = Math.max(1, Math.round(rect.height * ratio()));
    if (canvas.width === width && canvas.height === height) {
      return;
    }

    // Resizing a canvas clears it: the drawing is copied back, scaled to the new size (a layout change never loses it).
    let snapshot = null;
    if (!empty && canvas.width > 0 && canvas.height > 0) {
      snapshot = document.createElement("canvas");
      snapshot.width = canvas.width;
      snapshot.height = canvas.height;
      snapshot.getContext("2d").drawImage(canvas, 0, 0);
    }

    canvas.width = width;
    canvas.height = height;
    style();
    if (snapshot) {
      context.drawImage(snapshot, 0, 0, snapshot.width, snapshot.height, 0, 0, width / ratio(), height / ratio());
    }
  };

  const point = (event) => {
    const rect = canvas.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  };

  const down = (event) => {
    event.preventDefault();
    canvas.setPointerCapture(event.pointerId);
    drawing = true;
    last = point(event);
    context.beginPath();
    context.arc(last.x, last.y, 1.25, 0, Math.PI * 2);
    context.fill();
  };

  const move = (event) => {
    if (!drawing) {
      return;
    }

    event.preventDefault();
    const next = point(event);
    context.beginPath();
    context.moveTo(last.x, last.y);
    context.lineTo(next.x, next.y);
    context.stroke();
    last = next;
    if (empty) {
      empty = false;
      changed(true);
    }
  };

  const up = () => {
    if (!drawing) {
      return;
    }

    drawing = false;
    if (empty) {
      empty = false;
      changed(true);
    }
  };

  canvas.addEventListener("pointerdown", down);
  canvas.addEventListener("pointermove", move);
  canvas.addEventListener("pointerup", up);
  canvas.addEventListener("pointercancel", up);
  const observer = new ResizeObserver(resize);
  observer.observe(canvas);
  resize();
  style();

  return {
    clear() {
      context.save();
      context.setTransform(1, 0, 0, 1, 0, 0);
      context.clearRect(0, 0, canvas.width, canvas.height);
      context.restore();
      if (!empty) {
        empty = true;
        changed(false);
      }
    },
    isEmpty() {
      return empty;
    },
    toPng() {
      const output = document.createElement("canvas");
      output.width = canvas.width;
      output.height = canvas.height;
      const target = output.getContext("2d");
      target.fillStyle = "#ffffff";
      target.fillRect(0, 0, output.width, output.height);
      target.drawImage(canvas, 0, 0);
      return new Promise((resolve, reject) =>
        output.toBlob((blob) => (blob ? resolve(blob) : reject(new Error("The signature could not be encoded."))), "image/png"),
      );
    },
    dispose() {
      canvas.removeEventListener("pointerdown", down);
      canvas.removeEventListener("pointermove", move);
      canvas.removeEventListener("pointerup", up);
      canvas.removeEventListener("pointercancel", up);
      observer.disconnect();
    },
  };
}
