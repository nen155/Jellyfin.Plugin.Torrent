(function () {
    'use strict';

    var script = document.currentScript;
    var base = (script && script.src ? script.src : window.location.origin + '/Torrent/inject.js')
        .replace(/\/inject\.js(\?.*)?$/i, '');
    var configUrl = base + '/api/config';
    var suggestionsUrl = base + '/api/suggestions';
    var pageUrl = base + '/';
    var rowId = 'torrentDailySuggestionsRow';
    var buttonId = 'torrentOpenButton';

    var state = {
        config: null,
        suggestions: null,
        loadingConfig: false,
        loadingSuggestions: false
    };

    function accessToken() {
        try {
            if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
                var token = window.ApiClient.accessToken();
                if (token) { return token; }
            }
        } catch (e) { /* sin ApiClient */ }

        try {
            var creds = JSON.parse(window.localStorage.getItem('jellyfin_credentials') || '{}');
            var servers = creds.Servers || [];
            for (var i = 0; i < servers.length; i++) {
                if (servers[i] && servers[i].AccessToken) { return servers[i].AccessToken; }
            }
        } catch (e) { /* sin almacenamiento */ }

        return '';
    }

    function getJson(url) {
        return fetch(url, {
            headers: { Authorization: 'MediaBrowser Token="' + accessToken() + '"' },
            credentials: 'same-origin'
        }).then(function (response) {
            if (!response.ok) { throw new Error('HTTP ' + response.status); }
            return response.json();
        });
    }

    function proxied(url, referer) {
        if (!url) { return ''; }
        return base + '/api/image?u=' + encodeURIComponent(url)
            + '&r=' + encodeURIComponent(referer || '');
    }

    function el(tag, css, text) {
        var node = document.createElement(tag);
        if (css) { node.style.cssText = css; }
        if (text !== undefined && text !== null) { node.textContent = text; }
        return node;
    }

    function buildCard(item) {
        var link = el('a', 'flex:0 0 auto;width:132px;display:block;text-decoration:none;color:inherit;');
        link.href = pageUrl + '?q=' + encodeURIComponent(item.title || '');
        link.target = '_blank';
        link.rel = 'noopener';
        link.title = (item.title || '') + (item.detailUrl ? '\n' + item.detailUrl : '');

        var box = el('div', 'position:relative;width:132px;height:198px;border-radius:6px;overflow:hidden;'
            + 'background:rgba(127,127,127,.18);display:flex;align-items:center;justify-content:center;'
            + 'font-size:1.6rem;opacity:.95;');

        if (item.posterUrl) {
            var img = el('img', 'width:100%;height:100%;object-fit:cover;display:block;');
            img.loading = 'lazy';
            img.alt = '';
            img.src = proxied(item.posterUrl, item.detailUrl);
            img.onerror = function () { img.remove(); box.textContent = '🎞'; };
            box.appendChild(img);
        } else {
            box.textContent = '🎞';
        }

        link.appendChild(box);
        link.appendChild(el('div', 'font-size:.8rem;line-height:1.2;margin-top:.35rem;max-height:2.4em;'
            + 'overflow:hidden;', item.title || ''));
        var meta = [item.sourceName, item.resolution || item.category]
            .filter(function (value) { return !!value; })
            .join(' · ');
        link.appendChild(el('div', 'font-size:.7rem;opacity:.65;margin-top:.15rem;white-space:nowrap;'
            + 'overflow:hidden;text-overflow:ellipsis;', meta));
        return link;
    }

    function buildStrip(items) {
        var strip = el('div', 'display:flex;gap:.7rem;overflow-x:auto;padding:.25rem 1rem .5rem;');
        items.forEach(function (item) { strip.appendChild(buildCard(item)); });
        return strip;
    }

    function buildRow(suggestions) {
        var section = el('div', 'margin:.5rem 0 1.25rem;');
        section.id = rowId;

        var head = el('div', 'display:flex;align-items:baseline;gap:.75rem;padding:0 1rem;margin-bottom:.4rem;');
        head.appendChild(el('h2', 'font-size:1.2rem;font-weight:600;margin:0;', 'Sugerencias del día'));
        var more = el('a', 'font-size:.8rem;opacity:.8;text-decoration:none;color:inherit;', 'Ver todo');
        more.href = pageUrl;
        more.target = '_blank';
        more.rel = 'noopener';
        head.appendChild(more);
        section.appendChild(head);

        var movies = suggestions.movies || [];
        var series = suggestions.series || [];
        if (movies.length) {
            section.appendChild(el('div', 'font-size:.78rem;opacity:.65;padding:0 1rem;margin:.35rem 0 .15rem;',
                'Películas'));
            section.appendChild(buildStrip(movies));
        }

        if (series.length) {
            section.appendChild(el('div', 'font-size:.78rem;opacity:.65;padding:0 1rem;margin:.35rem 0 .15rem;',
                'Series'));
            section.appendChild(buildStrip(series));
        }

        return section;
    }

    function ensureButton() {
        if (!state.config || state.config.enabled === false) { return; }
        if (state.config.dailySuggestionsWebButton === false) { return; }
        if (document.getElementById(buttonId)) { return; }

        var button = el('a', 'position:fixed;right:12px;bottom:56px;z-index:9999;padding:8px 14px;'
            + 'border-radius:18px;background:rgba(0,0,0,.68);color:#fff;border:1px solid #555;'
            + 'font:13px sans-serif;text-decoration:none;box-shadow:0 4px 14px rgba(0,0,0,.4);',
            'Torrents');
        button.id = buttonId;
        button.href = pageUrl;
        button.target = '_blank';
        button.rel = 'noopener';
        button.title = 'Abrir el buscador de torrents';
        document.body.appendChild(button);
    }

    function ensureRow() {
        if (!state.config || state.config.enabled === false) { return; }
        if (state.config.dailySuggestionsHomeRow === false) { return; }
        if (!state.suggestions || state.suggestions.enabled === false) { return; }
        if (document.getElementById(rowId)) { return; }
        if (!(state.suggestions.movies || []).length && !(state.suggestions.series || []).length) { return; }

        var home = document.querySelector('#homePage .homeSections')
            || document.querySelector('.homeSections')
            || document.getElementById('homePage');
        if (!home) { return; }

        home.insertBefore(buildRow(state.suggestions), home.firstChild);
    }

    function onHome() {
        var hash = window.location.hash || '';
        return hash === '' || hash.indexOf('/home') >= 0;
    }

    function loadSuggestions() {
        if (state.loadingSuggestions || state.suggestions) { return; }
        state.loadingSuggestions = true;
        getJson(suggestionsUrl)
            .then(function (data) {
                state.suggestions = data;
                ensureRow();
            })
            .catch(function () { /* sin sesión o plugin desactivado */ })
            .then(function () { state.loadingSuggestions = false; });
    }

    function loadConfig() {
        if (state.loadingConfig || state.config) { return; }
        state.loadingConfig = true;
        getJson(configUrl)
            .then(function (config) { state.config = config || {}; })
            .catch(function () { state.config = {}; })
            .then(function () {
                state.loadingConfig = false;
                ensureButton();
            });
    }

    function tick() {
        loadConfig();
        ensureButton();
        if (!onHome()) { return; }
        if (state.suggestions) {
            ensureRow();
        } else {
            loadSuggestions();
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', tick);
    } else {
        tick();
    }

    window.addEventListener('hashchange', tick);
    setInterval(tick, 2500);
})();
