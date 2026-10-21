// Taps on a view of the damage schematic: the position, in percent of the view, is sent to .NET, which opens the damage
// dialog at that place. The keyboard and screen reader path is the list of damages and its "add a damage" button,
// which opens the same dialog.
export function attach(container, dotnet) {
  const onClick = (event) => {
    const view = event.target instanceof Element ? event.target.closest("svg[data-view]") : null;
    if (!view || !container.contains(view)) {
      return;
    }

    const rect = view.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) {
      return;
    }

    const clamp = (value) => Math.round(Math.min(100, Math.max(0, value)) * 100) / 100;
    const x = clamp(((event.clientX - rect.left) / rect.width) * 100);
    const y = clamp(((event.clientY - rect.top) / rect.height) * 100);
    dotnet.invokeMethodAsync("OnSchematicTapped", view.dataset.view, x, y);
  };

  container.addEventListener("click", onClick);
  return {
    dispose() {
      container.removeEventListener("click", onClick);
    },
  };
}
