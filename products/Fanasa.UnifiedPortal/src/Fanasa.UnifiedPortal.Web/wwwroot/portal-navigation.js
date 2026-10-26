(function () {
    'use strict';
    const organizationPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    function routesFor(organizationId, page) {
        const selected = typeof organizationId === 'string' && organizationPattern.test(organizationId)
            && organizationId !== '00000000-0000-0000-0000-000000000000' ? organizationId.toLowerCase() : null;
        const query = selected ? '?tenantId=' + selected : '';
        return {
            home: page === 'home' ? '#top' : '/' + query,
            workspace: page === 'home' ? '#workspace' : '/' + query + '#workspace',
            centers: page === 'home' ? '#centers' : '/' + query + '#centers',
            services: page === 'workspace' ? '#top' : '/Workspace' + query
        };
    }
    if (typeof module !== 'undefined' && module.exports) module.exports = { routesFor };
    if (typeof document === 'undefined') return;
    const links = Array.from(document.querySelectorAll('[data-portal-link]'));
    function setOrganization(organizationId) {
        // This is a navigation hint, never a membership or access grant. Each
        // destination still verifies the organization through the central reader.
        const routes = routesFor(organizationId, document.body.dataset.portalPage);
        for (const link of links) {
            const target = routes[link.dataset.portalLink];
            if (target) link.setAttribute('href', target);
        }
    }
    globalThis.FanasaPortalNavigation = { setOrganization };
    setOrganization(document.body.dataset.portalOrganization);
})();
