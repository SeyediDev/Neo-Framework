(function () {
    'use strict';
    const organizationPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    function organizationFor(organizationId) {
        return typeof organizationId === 'string' && organizationPattern.test(organizationId)
            && organizationId !== '00000000-0000-0000-0000-000000000000' ? organizationId.toLowerCase() : null;
    }
    function routesFor(organizationId, page) {
        const selected = organizationFor(organizationId);
        const query = selected ? '?tenantId=' + selected : '';
        return {
            home: page === 'home' ? '#top' : '/' + query,
            workspace: page === 'home' ? '#workspace' : '/' + query + '#workspace',
            centers: page === 'home' ? '#centers' : '/' + query + '#centers',
            services: page === 'workspace' ? '#top' : '/Workspace' + query
        };
    }
    function signInUrlFor(organizationId, page, section) {
        const selected = organizationFor(organizationId);
        const query = selected ? '?tenantId=' + selected : '';
        const workspace = page === 'workspace';
        const allowed = workspace ? ['catalog', 'requests', 'services', 'usage', 'publish', 'review'] : ['top', 'workspace', 'centers'];
        const fragment = typeof section === 'string' ? section.replace(/^#/, '') : '';
        const target = (workspace ? '/Workspace' : '/') + query + '#' + (allowed.includes(fragment) ? fragment : workspace ? 'catalog' : 'workspace');
        return '/login?returnUrl=' + encodeURIComponent(target);
    }
    if (typeof module !== 'undefined' && module.exports) module.exports = { routesFor, signInUrlFor };
    if (typeof document === 'undefined') return;
    const links = Array.from(document.querySelectorAll('[data-portal-link]'));
    const loginLinks = Array.from(document.querySelectorAll('[data-portal-login]'));
    let selectedOrganization = null;
    let selectedSection = typeof location === 'undefined' ? '' : location.hash;
    function updateSignInLinks() {
        const target = signInUrlFor(selectedOrganization, document.body.dataset.portalPage, selectedSection);
        for (const link of loginLinks) link.setAttribute('href', target);
    }
    function setSection(section) {
        const fragment = typeof section === 'string' ? section.replace(/^#/, '') : '';
        const allowed = document.body.dataset.portalPage === 'workspace'
            ? ['catalog', 'requests', 'services', 'usage', 'publish', 'review'] : ['top', 'workspace', 'centers'];
        // Skip/scroll anchors do not change the active workspace tab.
        if (!allowed.includes(fragment)) return;
        selectedSection = fragment;
        updateSignInLinks();
    }
    function setOrganization(organizationId) {
        // This is a navigation hint, never a membership or access grant. Each
        // destination still verifies the organization through the central reader.
        selectedOrganization = organizationFor(organizationId);
        const routes = routesFor(selectedOrganization, document.body.dataset.portalPage);
        for (const link of links) {
            const target = routes[link.dataset.portalLink];
            if (target) link.setAttribute('href', target);
        }
        updateSignInLinks();
    }
    globalThis.FanasaPortalNavigation = { setOrganization, setSection };
    if (typeof window !== 'undefined') window.addEventListener('hashchange', () => setSection(location.hash));
    setOrganization(document.body.dataset.portalOrganization);
})();
