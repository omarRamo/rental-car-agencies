// Progressive enhancement of the search forms of the public site: they work without this module (validation is done
// by the server). Unticking "return elsewhere" resets the hidden return location; the return follows the pick-up so
// that it is never before it (three days later by default, the usual minimum of the high season).
const pad = (value) => String(value).padStart(2, "0");

// Value of a datetime-local input (wall-clock time, no time zone: Tunis time on the server).
const toInputValue = (date) =>
  `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;

for (const form of document.querySelectorAll("form[data-search-form]")) {
  const elsewhere = form.querySelector("[data-return-elsewhere]");
  const returnLocation = form.querySelector("[data-return-location]");
  const pickup = form.querySelector("[data-pickup]");
  const dropOff = form.querySelector("[data-return]");

  elsewhere?.addEventListener("change", () => {
    if (!elsewhere.checked && returnLocation) {
      returnLocation.value = "";
    }
  });

  pickup?.addEventListener("change", () => {
    if (!dropOff || !pickup.value) {
      return;
    }

    dropOff.min = pickup.value;
    if (!dropOff.value || dropOff.value <= pickup.value) {
      const start = new Date(pickup.value);
      if (!Number.isNaN(start.getTime())) {
        start.setDate(start.getDate() + 3);
        dropOff.value = toInputValue(start);
      }
    }
  });
}
