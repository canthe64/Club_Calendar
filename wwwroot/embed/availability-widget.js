// Curling facility public availability widget.
// No credentials, no Graph logic - just fetches the app's public availability endpoint and
// renders it. Self-configuring: the API host is derived from wherever this script itself was
// loaded from, so the CMS embed snippet never needs a hostname edited into it.
(function () {
    var scriptEl = document.currentScript;
    var apiBase = new URL(scriptEl.src).origin;
    var targetId = scriptEl.getAttribute('data-target') || 'curling-availability';
    var days = scriptEl.getAttribute('data-days');

    function formatDate(iso) {
        var d = new Date(iso);
        return d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
    }

    function formatTime(iso) {
        var d = new Date(iso);
        return d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
    }

    function render(container, data) {
        container.innerHTML = '';

        if (data.clubEvents && data.clubEvents.length > 0) {
            var eventsBox = document.createElement('div');
            eventsBox.style.cssText = 'margin-bottom:12px';
            data.clubEvents.forEach(function (ce) {
                var row = document.createElement('div');
                row.style.cssText = 'padding:6px 10px;border-radius:6px;margin-bottom:4px;font-size:13px;font-weight:600;' +
                    (ce.marksSheetsUnavailable ? 'background:#fdf1f0;color:#a02c21;' : 'background:#f1efec;color:#5a5347;');
                var range = ce.isAllDay
                    ? (formatDate(ce.start) === formatDate(ce.end) ? formatDate(ce.start) : formatDate(ce.start) + ' - ' + formatDate(ce.end))
                    : formatDate(ce.start) + ', ' + formatTime(ce.start) + '-' + formatTime(ce.end);
                row.textContent = range + ': ' + ce.title + (ce.marksSheetsUnavailable ? ' - all sheets reserved' : '');
                eventsBox.appendChild(row);
            });
            container.appendChild(eventsBox);
        }

        if (!data.sheetSlots || data.sheetSlots.length === 0) {
            var empty = document.createElement('div');
            empty.style.cssText = 'color:#90a0ab;font-size:13px';
            empty.textContent = 'No open rental times posted right now - contact the club for availability.';
            container.appendChild(empty);
            return;
        }

        var list = document.createElement('div');
        list.style.cssText = 'display:flex;flex-direction:column;gap:6px';
        data.sheetSlots.forEach(function (slot) {
            var row = document.createElement('div');
            row.style.cssText = 'display:flex;gap:10px;align-items:center;border:1px solid #e7ecef;border-radius:6px;padding:7px 10px;font-size:13px';
            // Built with textContent, never innerHTML interpolation - this script runs on the
            // club's own website, so any HTML sneaking through server data would be stored XSS
            // there, not here. sheetLabel is admin-config-derived today, but don't depend on that.
            var label = document.createElement('span');
            label.style.cssText = 'font-weight:600;color:#2d5f8a';
            label.textContent = slot.sheetLabel;
            var date = document.createElement('span');
            date.style.cssText = 'color:#1e2a33';
            date.textContent = formatDate(slot.start);
            var time = document.createElement('span');
            time.style.cssText = 'color:#5a7183';
            time.textContent = formatTime(slot.start) + ' - ' + formatTime(slot.end);
            row.appendChild(label);
            row.appendChild(date);
            row.appendChild(time);
            list.appendChild(row);
        });
        container.appendChild(list);
    }

    function init() {
        var container = document.getElementById(targetId);
        if (!container) {
            return;
        }

        container.textContent = 'Loading availability...';

        var url = apiBase + '/api/public/availability' + (days ? '?days=' + encodeURIComponent(days) : '');
        fetch(url)
            .then(function (response) {
                if (!response.ok) {
                    throw new Error('Request failed: ' + response.status);
                }
                return response.json();
            })
            .then(function (data) { render(container, data); })
            .catch(function () {
                container.textContent = 'Availability is temporarily unavailable - please check back later.';
            });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
