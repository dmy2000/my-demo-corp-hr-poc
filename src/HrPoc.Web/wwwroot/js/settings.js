// My Demo Corp — HR System (PoC) v1.3 Settings Application

let settingsState = {
  hasClientSecret: false,
  latestToken: null,
  computedResourceUri: ''
};

// Utility: API caller with relative path support for IIS virtual directories
async function api(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  const res = await fetch(path, { ...options, headers });
  let data = null;
  const text = await res.text();
  if (text) {
    try { data = JSON.parse(text); } catch { data = { message: text }; }
  }
  if (!res.ok) {
    throw new Error((data && (data.message || data.error)) || `HTTP ${res.status}: ${res.statusText}`);
  }
  return data;
}

function showToast(message, type = 'success') {
  const container = document.getElementById('toastContainer');
  const el = document.createElement('div');
  el.className = `toast toast-${type}`;
  el.textContent = message;
  container.appendChild(el);
  setTimeout(() => el.remove(), 4000);
}

function esc(val) {
  if (val === null || val === undefined) return '';
  return String(val)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function formatDateTime(dt) {
  if (!dt) return '—';
  const d = new Date(dt);
  if (isNaN(d.getTime())) return String(dt);
  return d.toISOString().replace('T', ' ').slice(0, 19) + ' UTC';
}

// Copy to clipboard helper
async function copyToClipboard(text, btnElement) {
  if (!text) return;
  try {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      await navigator.clipboard.writeText(text);
    } else {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      ta.remove();
    }
    if (btnElement) {
      const origText = btnElement.innerHTML;
      btnElement.classList.add('copied');
      btnElement.innerHTML = '&#10003; Copied!';
      setTimeout(() => {
        btnElement.classList.remove('copied');
        btnElement.innerHTML = origText;
      }, 2000);
    }
    showToast('Copied to clipboard!', 'success');
  } catch (err) {
    showToast('Could not copy to clipboard: ' + err.message, 'error');
  }
}

// DOM Initialization
document.addEventListener('DOMContentLoaded', async () => {
  bindUIEvents();
  await loadSettings();
});

function bindUIEvents() {
  // Toggle card visual feedback
  const useOidcWeb = document.getElementById('useOidcForWeb');
  const cardWeb = document.getElementById('cardUseOidcForWeb');
  const badgeWeb = document.getElementById('badgeWebOidc');
  useOidcWeb.addEventListener('change', () => {
    updateToggleCardState(cardWeb, badgeWeb, useOidcWeb.checked);
  });

  const useOidcOData = document.getElementById('useOidcForOData');
  const cardOData = document.getElementById('cardUseOidcForOData');
  const badgeOData = document.getElementById('badgeODataOidc');
  useOidcOData.addEventListener('change', () => {
    updateToggleCardState(cardOData, badgeOData, useOidcOData.checked);
  });

  // Mode cards selection (Mode A vs Mode B)
  const cardModeA = document.getElementById('cardModeA');
  const cardModeB = document.getElementById('cardModeB');
  const radioModeA = document.getElementById('radioModeA');
  const radioModeB = document.getElementById('radioModeB');

  cardModeA.addEventListener('click', () => {
    radioModeA.checked = true;
    cardModeA.classList.add('selected');
    cardModeB.classList.remove('selected');
  });

  cardModeB.addEventListener('click', () => {
    radioModeB.checked = true;
    cardModeB.classList.add('selected');
    cardModeA.classList.remove('selected');
  });

  radioModeA.addEventListener('change', () => {
    if (radioModeA.checked) {
      cardModeA.classList.add('selected');
      cardModeB.classList.remove('selected');
    }
  });

  radioModeB.addEventListener('change', () => {
    if (radioModeB.checked) {
      cardModeB.classList.add('selected');
      cardModeA.classList.remove('selected');
    }
  });

  // Client secret show/hide toggle
  const secretInput = document.getElementById('oidcClientSecret');
  const btnToggleSecret = document.getElementById('btnToggleSecret');
  btnToggleSecret.addEventListener('click', () => {
    if (secretInput.type === 'password') {
      secretInput.type = 'text';
      btnToggleSecret.textContent = 'Hide';
    } else {
      secretInput.type = 'password';
      btnToggleSecret.textContent = 'Show';
    }
  });

  // Helper button: + Add AuthZ Scopes (hr:read hr:write)
  const btnAddAuthzScopes = document.getElementById('btnAddAuthzScopes');
  btnAddAuthzScopes.addEventListener('click', () => {
    const scopesInput = document.getElementById('oidcScopes');
    const readScope = (document.getElementById('authzReadScope').value || 'hr:read').trim();
    const writeScope = (document.getElementById('authzWriteScope').value || 'hr:write').trim();

    let current = (scopesInput.value || '').trim();
    const parts = current.split(/\s+/).filter(Boolean);

    let addedCount = 0;
    if (readScope && !parts.includes(readScope)) {
      parts.push(readScope);
      addedCount++;
    }
    if (writeScope && !parts.includes(writeScope)) {
      parts.push(writeScope);
      addedCount++;
    }

    scopesInput.value = parts.join(' ');
    if (addedCount > 0) {
      showToast(`Added ${readScope} and ${writeScope} to requested scopes`, 'success');
    } else {
      showToast('Scopes are already present in requested scopes list', 'success');
    }
  });

  // Copy buttons for redirect URIs
  document.getElementById('btnCopyRedirectUri').addEventListener('click', (e) => {
    copyToClipboard(document.getElementById('computedRedirectUriVal').value, e.currentTarget);
  });
  document.getElementById('btnCopySigninOidcUri').addEventListener('click', (e) => {
    copyToClipboard(document.getElementById('computedSigninOidcUriVal').value, e.currentTarget);
  });

  // Authorization Server helper & live copy fields sync
  document.getElementById('btnUseServerResourceUri').addEventListener('click', () => {
    if (settingsState.computedResourceUri) {
      document.getElementById('authzAudience').value = settingsState.computedResourceUri;
      syncAuthzCopyFields();
      showToast('Resource Identifier updated to current server URL', 'success');
    }
  });
  ['authzAudience', 'authzReadScope', 'authzWriteScope'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('input', syncAuthzCopyFields);
  });
  document.getElementById('btnCopyAuthzResource').addEventListener('click', (e) => {
    copyToClipboard(document.getElementById('copyAuthzResourceVal').value, e.currentTarget);
  });
  document.getElementById('btnCopyAuthzAudiences').addEventListener('click', (e) => {
    copyToClipboard(document.getElementById('copyAuthzAudiencesVal').value, e.currentTarget);
  });
  document.getElementById('btnCopyAuthzODataAudiences').addEventListener('click', (e) => {
    copyToClipboard(document.getElementById('copyAuthzODataAudiencesVal').value, e.currentTarget);
  });
  document.getElementById('btnCopyAuthzRestAudiences').addEventListener('click', (e) => {
    copyToClipboard(document.getElementById('copyAuthzRestAudiencesVal').value, e.currentTarget);
  });
  document.getElementById('btnCopyAuthzReadScope').addEventListener('click', (e) => {
    copyToClipboard(document.getElementById('copyAuthzReadScopeVal').value, e.currentTarget);
  });
  document.getElementById('btnCopyAuthzWriteScope').addEventListener('click', (e) => {
    copyToClipboard(document.getElementById('copyAuthzWriteScopeVal').value, e.currentTarget);
  });

  // Diagnostic Test: OIDC Discovery
  document.getElementById('btnTestDiscovery').addEventListener('click', runDiscoveryTest);

  // Tool A: Request Token (client_credentials)
  document.getElementById('btnTestToken').addEventListener('click', runTokenTest);

  // Tool B: Bearer JWT Inspector & Simulator
  document.getElementById('btnInspectToken').addEventListener('click', runTokenInspection);

  // Save buttons (Top & Bottom)
  document.getElementById('btnSaveSettingsTop').addEventListener('click', saveSettings);
  document.getElementById('btnSaveSettingsBottom').addEventListener('click', saveSettings);
}

function syncAuthzCopyFields() {
  const rawAud = (document.getElementById('authzAudience').value || '').trim();
  const firstEntry = rawAud.split(/[,;]+/).map(s => s.trim()).filter(Boolean)[0] || settingsState.computedResourceUri || 'https://mydemocorp.com/hr';
  const baseRes = firstEntry
    .replace(/\/+$/, '')
    .replace(/\/(odata(\/(Employees|Departments|CostCenters|Locations))?|api(\/(employees|departments|costcenters|locations))?)$/i, '');
  const readScope = (document.getElementById('authzReadScope').value || 'hr:read').trim();
  const writeScope = (document.getElementById('authzWriteScope').value || 'hr:write').trim();

  const odataList = [
    `${baseRes}/odata/Employees`,
    `${baseRes}/odata/Departments`,
    `${baseRes}/odata/CostCenters`,
    `${baseRes}/odata/Locations`
  ].join(', ');

  const restList = [
    `${baseRes}/api/employees`,
    `${baseRes}/api/departments`,
    `${baseRes}/api/costcenters`,
    `${baseRes}/api/locations`
  ].join(', ');

  document.getElementById('copyAuthzResourceVal').value = baseRes;
  document.getElementById('copyAuthzAudiencesVal').value = `${odataList}, ${restList}`;
  document.getElementById('copyAuthzODataAudiencesVal').value = odataList;
  document.getElementById('copyAuthzRestAudiencesVal').value = restList;
  document.getElementById('copyAuthzReadScopeVal').value = readScope;
  document.getElementById('copyAuthzWriteScopeVal').value = writeScope;
}

function updateToggleCardState(cardEl, badgeEl, isChecked) {
  if (isChecked) {
    cardEl.classList.add('active');
    badgeEl.className = 'sec-pill active';
    badgeEl.textContent = 'Active';
  } else {
    cardEl.classList.remove('active');
    badgeEl.className = 'sec-pill inactive';
    badgeEl.textContent = 'Disabled';
  }
}

async function loadSettings() {
  try {
    const data = await api('api/settings');

    // Section 1: Modes
    const useOidcWeb = document.getElementById('useOidcForWeb');
    useOidcWeb.checked = !!data.useOidcForWeb;
    updateToggleCardState(document.getElementById('cardUseOidcForWeb'), document.getElementById('badgeWebOidc'), useOidcWeb.checked);

    const useOidcOData = document.getElementById('useOidcForOData');
    useOidcOData.checked = !!data.useOidcForOData;
    updateToggleCardState(document.getElementById('cardUseOidcForOData'), document.getElementById('badgeODataOidc'), useOidcOData.checked);

    const radioModeA = document.getElementById('radioModeA');
    const radioModeB = document.getElementById('radioModeB');
    const cardModeA = document.getElementById('cardModeA');
    const cardModeB = document.getElementById('cardModeB');

    if (data.useAuthorizationServer) {
      radioModeB.checked = true;
      cardModeB.classList.add('selected');
      cardModeA.classList.remove('selected');
    } else {
      radioModeA.checked = true;
      cardModeA.classList.add('selected');
      cardModeB.classList.remove('selected');
    }

    // Section 2: OIDC Provider
    document.getElementById('oidcAuthority').value = data.oidcAuthority || '';
    document.getElementById('oidcClientId').value = data.oidcClientId || '';

    settingsState.hasClientSecret = !!data.hasClientSecret;
    const secretInput = document.getElementById('oidcClientSecret');
    const secretHelp = document.getElementById('secretHelpText');
    if (data.hasClientSecret) {
      secretInput.value = '••••••••';
      secretHelp.innerHTML = 'Client Secret is securely stored in <code>dbo.InstallationConfig</code>. Leaving as <code>••••••••</code> keeps it unchanged.';
    } else {
      secretInput.value = '';
      secretHelp.innerHTML = 'Enter the Client Secret issued by your OpenID Connect Identity Provider.';
    }

    document.getElementById('oidcScopes').value = data.oidcScopes || 'openid profile email';
    document.getElementById('oidcRedirectUri').value = data.oidcRedirectUri || '';
    document.getElementById('oidcRequireHttpsMetadata').checked = !!data.oidcRequireHttpsMetadata;

    // Computed Redirect URIs & Resource URI
    settingsState.computedResourceUri = data.computedResourceUri || `${window.location.origin}/hr`;
    document.getElementById('computedRedirectUriVal').value = data.computedRedirectUri || `${window.location.origin}/hr/api/auth/callback`;
    document.getElementById('computedSigninOidcUriVal').value = data.computedSigninOidcUri || `${window.location.origin}/hr/signin-oidc`;

    // Section 3: AuthZ Server
    document.getElementById('authzAudience').value = data.authzAudience || 'https://mydemocorp.com/hr';
    document.getElementById('authzServerIssuer').value = data.authzServerIssuer || '';
    document.getElementById('authzReadScope').value = data.authzReadScope || 'hr:read';
    document.getElementById('authzWriteScope').value = data.authzWriteScope || 'hr:write';
    document.getElementById('authzRequiredClaimType').value = data.authzRequiredClaimType || '';
    document.getElementById('authzRequiredClaimValue').value = data.authzRequiredClaimValue || '';
    syncAuthzCopyFields();

    // Section 5: Metadata
    document.getElementById('metaIisSite').textContent = data.iisSiteName || '—';
    document.getElementById('metaIisSubPath').textContent = data.iisAppSubPath || '—';
    document.getElementById('metaAppPool').textContent = data.appPoolName || '—';
    document.getElementById('metaInstallPath').textContent = data.installPath || '—';
    document.getElementById('metaSqlLogin').textContent = data.appSqlLogin || '—';
    document.getElementById('metaVersion').textContent = `v${data.version || '1.3.0'} • ${formatDateTime(data.updatedAtUtc || data.installedAtUtc)}`;

  } catch (err) {
    showToast(`Failed to load settings: ${err.message}`, 'error');
  }
}

async function saveSettings() {
  const btnTop = document.getElementById('btnSaveSettingsTop');
  const btnBottom = document.getElementById('btnSaveSettingsBottom');
  const statusMsg = document.getElementById('settingsSaveStatus');

  const origTopText = btnTop.innerHTML;
  const origBottomText = btnBottom.innerHTML;

  btnTop.disabled = true;
  btnBottom.disabled = true;
  btnTop.innerHTML = '&#8987; Saving...';
  btnBottom.innerHTML = '&#8987; Saving...';
  if (statusMsg) statusMsg.textContent = 'Saving configuration to SQL Server...';

  try {
    const radioModeB = document.getElementById('radioModeB');
    const secretVal = document.getElementById('oidcClientSecret').value.trim();

    const payload = {
      useOidcForWeb: document.getElementById('useOidcForWeb').checked,
      useOidcForOData: document.getElementById('useOidcForOData').checked,
      useAuthorizationServer: radioModeB.checked,
      oidcAuthority: document.getElementById('oidcAuthority').value.trim(),
      oidcClientId: document.getElementById('oidcClientId').value.trim(),
      oidcClientSecret: secretVal,
      keepExistingSecret: (secretVal === '••••••••' || (secretVal === '' && settingsState.hasClientSecret)),
      oidcRedirectUri: document.getElementById('oidcRedirectUri').value.trim(),
      oidcScopes: document.getElementById('oidcScopes').value.trim() || 'openid profile email',
      oidcRequireHttpsMetadata: document.getElementById('oidcRequireHttpsMetadata').checked,
      authzServerIssuer: document.getElementById('authzServerIssuer').value.trim(),
      authzAudience: document.getElementById('authzAudience').value.trim(),
      authzReadScope: document.getElementById('authzReadScope').value.trim() || 'hr:read',
      authzWriteScope: document.getElementById('authzWriteScope').value.trim() || 'hr:write',
      authzRequiredClaimType: document.getElementById('authzRequiredClaimType').value.trim(),
      authzRequiredClaimValue: document.getElementById('authzRequiredClaimValue').value.trim()
    };

    const updated = await api('api/settings', {
      method: 'PUT',
      body: JSON.stringify(payload)
    });

    showToast('Settings saved successfully to dbo.InstallationConfig!', 'success');
    if (statusMsg) statusMsg.textContent = `Last saved at ${formatDateTime(new Date())}`;

    // Refresh display
    document.getElementById('metaVersion').textContent = `v${updated.version || '1.3.0'} • ${formatDateTime(updated.updatedAtUtc)}`;
    if (updated.hasClientSecret) {
      settingsState.hasClientSecret = true;
      document.getElementById('oidcClientSecret').value = '••••••••';
    }

  } catch (err) {
    showToast(`Error saving settings: ${err.message}`, 'error');
    if (statusMsg) statusMsg.textContent = `Save failed: ${err.message}`;
  } finally {
    btnTop.disabled = false;
    btnBottom.disabled = false;
    btnTop.innerHTML = origTopText;
    btnBottom.innerHTML = origBottomText;
  }
}

// Diagnostic Test: OIDC Discovery
async function runDiscoveryTest() {
  const btn = document.getElementById('btnTestDiscovery');
  const panel = document.getElementById('discoveryResultPanel');
  const authority = document.getElementById('oidcAuthority').value.trim();
  const requireHttps = document.getElementById('oidcRequireHttpsMetadata').checked;

  if (!authority) {
    showToast('Please enter an OIDC Authority URL first', 'error');
    document.getElementById('oidcAuthority').focus();
    return;
  }

  btn.disabled = true;
  btn.innerHTML = '&#8987; Testing Discovery...';
  panel.classList.remove('hidden');
  panel.innerHTML = '<div style="color:#94a3b8;">Contacting OIDC discovery endpoint (.well-known/openid-configuration)...</div>';

  try {
    const res = await api('api/settings/test-discovery', {
      method: 'POST',
      body: JSON.stringify({ authority, requireHttpsMetadata: requireHttps })
    });

    if (res.success) {
      const scopesPills = (res.scopesSupported || []).map(s => `<span class="scope-pill">${esc(s)}</span>`).join(' ');
      panel.innerHTML = `
        <div class="diag-panel-header">
          <span style="color:#4ade80;">&#10003; OIDC Discovery Succeeded (HTTP 200)</span>
          <span style="color:#94a3b8; font-weight:normal;">Keys: ${res.signingKeysCount} signing keys</span>
        </div>
        <div class="diag-grid">
          <div class="diag-key">Authority Checked:</div>
          <div class="diag-val">${esc(res.authority)}</div>

          <div class="diag-key">Issuer (iss):</div>
          <div class="diag-val success">${esc(res.issuer)}</div>

          <div class="diag-key">Authorization Endpoint:</div>
          <div class="diag-val">${esc(res.authorizationEndpoint)}</div>

          <div class="diag-key">Token Endpoint:</div>
          <div class="diag-val">${esc(res.tokenEndpoint)}</div>

          <div class="diag-key">Userinfo Endpoint:</div>
          <div class="diag-val">${esc(res.userinfoEndpoint || '—')}</div>

          <div class="diag-key">JWKS URI:</div>
          <div class="diag-val">${esc(res.jwksUri)}</div>

          <div class="diag-key">Signing Keys Count:</div>
          <div class="diag-val">${res.signingKeysCount} keys loaded &amp; ready</div>

          <div class="diag-key">Supported Scopes:</div>
          <div class="diag-val"><div class="scopes-pills-list">${scopesPills || '—'}</div></div>
        </div>
      `;
      showToast('OIDC Discovery metadata retrieved successfully!', 'success');
    } else {
      panel.innerHTML = `
        <div class="diag-panel-header">
          <span style="color:#f87171;">&#10007; OIDC Discovery Failed</span>
        </div>
        <div style="color:#fca5a5; margin-bottom: 8px;">${esc(res.error || 'Unknown error occurred.')}</div>
        <div style="font-size:11.5px; color:#94a3b8;">
          Verify that the Authority URL is accessible from the Windows Server/IIS host and that DNS and HTTPS certificates are valid.
        </div>
      `;
      showToast('OIDC Discovery failed. Check error details.', 'error');
    }
  } catch (err) {
    panel.innerHTML = `
      <div class="diag-panel-header"><span style="color:#f87171;">&#10007; Request Error</span></div>
      <div style="color:#fca5a5;">${esc(err.message)}</div>
    `;
    showToast(err.message, 'error');
  } finally {
    btn.disabled = false;
    btn.innerHTML = '&#128269; Test OIDC Discovery (.well-known/openid-configuration)';
  }
}

// Tool A: Request M2M Access Token (client_credentials)
async function runTokenTest() {
  const btn = document.getElementById('btnTestToken');
  const panel = document.getElementById('tokenResultPanel');

  const authority = document.getElementById('oidcAuthority').value.trim();
  const clientId = document.getElementById('oidcClientId').value.trim();
  const clientSecret = document.getElementById('oidcClientSecret').value.trim();
  const audience = document.getElementById('authzAudience').value.trim();
  const readScope = document.getElementById('authzReadScope').value.trim() || 'hr:read';
  const writeScope = document.getElementById('authzWriteScope').value.trim() || 'hr:write';
  const scopes = `${readScope} ${writeScope}`.trim();

  if (!authority || !clientId) {
    showToast('Please enter OIDC Authority and Client ID first', 'error');
    return;
  }

  btn.disabled = true;
  btn.innerHTML = '&#8987; Requesting Token...';
  panel.classList.remove('hidden');
  panel.innerHTML = '<div style="color:#94a3b8;">Requesting OAuth 2.0 client_credentials token from token endpoint...</div>';

  try {
    const res = await api('api/settings/test-token', {
      method: 'POST',
      body: JSON.stringify({
        authority,
        clientId,
        clientSecret,
        authzAudience: audience,
        scopes
      })
    });

    if (res.success) {
      let accessToken = '';
      try {
        if (res.rawResponse) {
          const parsed = JSON.parse(res.rawResponse);
          accessToken = parsed.access_token || '';
        }
      } catch { }

      settingsState.latestToken = accessToken;

      const headerJson = res.decodedHeader ? JSON.stringify(res.decodedHeader, null, 2) : '—';
      const payloadJson = res.decodedPayload ? JSON.stringify(res.decodedPayload, null, 2) : '—';

      panel.innerHTML = `
        <div class="diag-panel-header">
          <span style="color:#4ade80;">&#10003; Access Token Successfully Issued (HTTP 200)</span>
          <div style="display:flex; gap:8px;">
            ${accessToken ? `
              <button type="button" class="btn btn-xs btn-outline" id="btnCopyAccessToken" style="color:#fff; border-color:#475569;">
                &#128203; Copy Access Token
              </button>
              <button type="button" class="btn btn-xs btn-primary" id="btnInspectReturnedToken">
                Inspect Token Below &darr;
              </button>
            ` : ''}
          </div>
        </div>
        <div class="diag-grid">
          <div class="diag-key">Token Type:</div>
          <div class="diag-val">${esc(res.tokenType || 'Bearer')}</div>

          <div class="diag-key">Expires In:</div>
          <div class="diag-val">${res.expiresIn ? `${res.expiresIn} seconds (${Math.round(res.expiresIn / 60)} min)` : '—'}</div>

          <div class="diag-key">Granted Scope:</div>
          <div class="diag-val">${esc(res.scope || '—')}</div>

          <div class="diag-key">Token Audience (aud):</div>
          <div class="diag-val success">${esc(res.audience || '—')}</div>
        </div>

        <div style="margin-top: 10px;">
          <strong style="color:#94a3b8; font-size:11px; text-transform:uppercase;">Decoded JWT Payload:</strong>
          <pre class="code-pre">${esc(payloadJson)}</pre>
        </div>

        <div style="margin-top: 8px;">
          <strong style="color:#94a3b8; font-size:11px; text-transform:uppercase;">Decoded JWT Header:</strong>
          <pre class="code-pre">${esc(headerJson)}</pre>
        </div>
      `;

      if (accessToken) {
        document.getElementById('btnCopyAccessToken').addEventListener('click', (e) => {
          copyToClipboard(accessToken, e.currentTarget);
        });
        document.getElementById('btnInspectReturnedToken').addEventListener('click', () => {
          const ta = document.getElementById('inspectTokenInput');
          ta.value = accessToken;
          ta.scrollIntoView({ behavior: 'smooth', block: 'center' });
          runTokenInspection();
        });
      }

      showToast('Access token successfully retrieved from the Token Endpoint!', 'success');
    } else {
      panel.innerHTML = `
        <div class="diag-panel-header"><span style="color:#f87171;">&#10007; Token Request Failed</span></div>
        <div style="color:#fca5a5; margin-bottom: 8px;">${esc(res.error || 'Unknown error occurred.')}</div>
        ${res.rawResponse ? `<pre class="code-pre">${esc(res.rawResponse)}</pre>` : ''}
      `;
      showToast('Token request failed. Check error details.', 'error');
    }
  } catch (err) {
    panel.innerHTML = `
      <div class="diag-panel-header"><span style="color:#f87171;">&#10007; Request Error</span></div>
      <div style="color:#fca5a5;">${esc(err.message)}</div>
    `;
    showToast(err.message, 'error');
  } finally {
    btn.disabled = false;
    btn.innerHTML = '&#9654; Request Token from Token Endpoint (client_credentials)';
  }
}

// Tool B: Bearer JWT Token Inspector & Simulator
async function runTokenInspection() {
  const btn = document.getElementById('btnInspectToken');
  const panel = document.getElementById('inspectResultPanel');
  const token = document.getElementById('inspectTokenInput').value.trim();

  if (!token) {
    showToast('Please paste a Bearer JWT access token to inspect', 'error');
    document.getElementById('inspectTokenInput').focus();
    return;
  }

  btn.disabled = true;
  btn.innerHTML = '&#8987; Validating...';
  panel.classList.remove('hidden');
  panel.innerHTML = '<div style="color:#94a3b8;">Verifying token signature, expiration, audience, and policy permissions...</div>';

  try {
    const res = await api('api/settings/inspect-token', {
      method: 'POST',
      body: JSON.stringify({ token })
    });

    if (res.success) {
      const readBadge = res.canRead
        ? '<span class="perm-pill full">&#10003; ALLOWED</span>'
        : `<span class="perm-pill readonly">&#10007; DENIED (${esc(res.readDenyReason || 'Missing required read scope')})</span>`;

      const writeBadge = res.canWrite
        ? '<span class="perm-pill full">&#10003; ALLOWED</span>'
        : `<span class="perm-pill readonly">&#10007; DENIED (${esc(res.writeDenyReason || 'Missing required write scope')})</span>`;

      const scopesList = (res.scopes || []).map(s => `<span class="scope-pill">${esc(s)}</span>`).join(' ') || '<span style="color:#94a3b8;">(None)</span>';

      let claimsRows = '';
      if (res.claims) {
        for (const [k, vals] of Object.entries(res.claims)) {
          claimsRows += `
            <tr>
              <td style="color:#38bdf8; font-family:monospace; font-weight:600;">${esc(k)}</td>
              <td style="font-family:monospace; color:#cbd5e1;">${esc(Array.isArray(vals) ? vals.join(', ') : vals)}</td>
            </tr>
          `;
        }
      }

      panel.innerHTML = `
        <div class="diag-panel-header">
          <span style="color:#4ade80;">&#10003; Token Verification &amp; Signature Succeeded</span>
          <span style="color:#94a3b8; font-weight:normal;">Expires: ${formatDateTime(res.expiresAtUtc)}</span>
        </div>

        <div style="background:#1e293b; border-radius:6px; padding:12px; margin-bottom:12px; display:flex; flex-direction:column; gap:8px;">
          <div style="display:flex; align-items:center; justify-content:space-between;">
            <strong style="color:#e2e8f0;">Read Operations (GET):</strong>
            <div>${readBadge}</div>
          </div>
          <div style="display:flex; align-items:center; justify-content:space-between;">
            <strong style="color:#e2e8f0;">Write Operations (POST, PUT, PATCH, DELETE):</strong>
            <div>${writeBadge}</div>
          </div>
        </div>

        <div class="diag-grid">
          <div class="diag-key">Subject (sub):</div>
          <div class="diag-val">${esc(res.subject || '—')}</div>

          <div class="diag-key">Issuer (iss):</div>
          <div class="diag-val">${esc(res.issuer || '—')}</div>

          <div class="diag-key">Audience (aud):</div>
          <div class="diag-val success">${esc(res.audience || '—')}</div>

          <div class="diag-key">Granted Scopes:</div>
          <div class="diag-val"><div class="scopes-pills-list">${scopesList}</div></div>
        </div>

        <div style="margin-top: 14px;">
          <strong style="color:#94a3b8; font-size:11px; text-transform:uppercase;">Decoded JWT Claims (${Object.keys(res.claims || {}).length}):</strong>
          <div style="overflow-x:auto; margin-top:6px;">
            <table class="claims-table">
              <thead>
                <tr>
                  <th style="background:#1e293b; color:#94a3b8; border-color:#334155;">Claim Type</th>
                  <th style="background:#1e293b; color:#94a3b8; border-color:#334155;">Value</th>
                </tr>
              </thead>
              <tbody>
                ${claimsRows || '<tr><td colspan="2" style="color:#94a3b8;">No claims extracted</td></tr>'}
              </tbody>
            </table>
          </div>
        </div>
      `;
      showToast('Token verified and evaluated against current policy!', 'success');
    } else {
      panel.innerHTML = `
        <div class="diag-panel-header"><span style="color:#f87171;">&#10007; Token Validation Failed</span></div>
        <div style="color:#fca5a5; margin-bottom: 8px;">${esc(res.error || 'Token validation failed.')}</div>
        <div style="font-size:11.5px; color:#94a3b8;">
          Ensure the token is signed with keys from the configured OIDC Authority, is not expired, and contains the required audience.
        </div>
      `;
      showToast('Token validation failed. Check error details.', 'error');
    }
  } catch (err) {
    panel.innerHTML = `
      <div class="diag-panel-header"><span style="color:#f87171;">&#10007; Request Error</span></div>
      <div style="color:#fca5a5;">${esc(err.message)}</div>
    `;
    showToast(err.message, 'error');
  } finally {
    btn.disabled = false;
    btn.innerHTML = '&#128269; Validate &amp; Check Permissions';
  }
}
