// Keyboard shortcuts of the quick booking screen (Alt+Shift+key, listed on the page): they move the focus to a field
// or press a control of the page, nothing else. Alt+Shift avoids the shortcuts of the browsers (Alt+D, Alt+F...).
const targets = {
    KeyP: '#booking-pickup',
    KeyC: '#booking-driver',
    KeyN: '[data-testid="booking-driver-new"]',
    KeyO: '#booking-outcome-option',
    KeyF: '#booking-outcome-confirmed',
    KeyS: '#booking-save',
};

function resolve(code) {
    const digit = /^Digit([1-9])$/.exec(code);
    if (digit) {
        return document.querySelector(`#booking-categories [data-category-index="${digit[1]}"] input[type="radio"]`);
    }

    const selector = targets[code];
    if (!selector) {
        return null;
    }

    const element = document.querySelector(selector);
    if (!element || element.matches('input, button, select, textarea')) {
        return element;
    }

    return element.querySelector('input, button') ?? element;
}

function isTextField(element) {
    return element.matches('textarea, select, input:not([type="radio"]):not([type="checkbox"]):not([type="button"]):not([type="submit"])');
}

export function register() {
    const onKeyDown = (event) => {
        if (!event.altKey || !event.shiftKey || event.ctrlKey || event.metaKey) {
            return;
        }

        const element = resolve(event.code);
        if (!element || element.disabled) {
            return;
        }

        event.preventDefault();
        element.focus();
        if (!isTextField(element)) {
            element.click();
        }
    };

    document.addEventListener('keydown', onKeyDown);
    return {
        dispose: () => document.removeEventListener('keydown', onKeyDown),
    };
}
