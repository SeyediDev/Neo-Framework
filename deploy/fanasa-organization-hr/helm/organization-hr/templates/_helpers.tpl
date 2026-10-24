{{- define "organization-hr.name" -}}
{{- printf "%s-organization-hr" .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "organization-hr.selector" -}}
app.kubernetes.io/name: organization-hr
app.kubernetes.io/instance: {{ .Release.Name | quote }}
{{- end -}}

{{- define "organization-hr.labels" -}}
{{ include "organization-hr.selector" . }}
app.kubernetes.io/managed-by: {{ .Release.Service | quote }}
fanasa.net/application: {{ .Values.applicationId | quote }}
fanasa.net/tenant: {{ .Values.tenantId | quote }}
fanasa.net/environment: development
fanasa.net/product: organization-fabric
fanasa.net/center: fanasa.ayin
{{- end -}}

{{- define "organization-hr.web-image" -}}
{{- printf "%s@%s" .Values.image.web.repository .Values.image.web.digest -}}
{{- end -}}

{{- define "organization-hr.workbench-image" -}}
{{- printf "%s@%s" .Values.image.workbench.repository .Values.image.workbench.digest -}}
{{- end -}}

{{- define "organization-hr.validate" -}}
{{- if .Values.gatewayRelay.enabled -}}
{{- $_ := required "A single host Gateway source /32 is required." .Values.gatewayRelay.nodeSourceCidr -}}
{{- if or (ne .Values.network.gateway.namespace .Release.Namespace) (ne (toJson .Values.network.gateway.podLabels) (toJson (dict "app.kubernetes.io/name" "organization-hr-gateway-relay" "app.kubernetes.io/instance" .Release.Name))) -}}
{{- fail "Relay mode requires the exact same-namespace relay Pod selector." -}}
{{- end -}}
{{- end -}}
{{- $_ := required "A verified web image repository is required." .Values.image.web.repository -}}
{{- $_ := required "An immutable web image digest is required." .Values.image.web.digest -}}
{{- $_ := required "A verified workbench image repository is required." .Values.image.workbench.repository -}}
{{- $_ := required "An immutable workbench image digest is required." .Values.image.workbench.digest -}}
{{- $_ := required "An existing persistent-state claim is required." .Values.persistence.existingClaim -}}
{{- $_ := required "An external runtime configuration Secret is required." .Values.configuration.existingSecret -}}
{{- $_ := required "An external upstream TLS Secret is required." .Values.tls.existingSecret -}}
{{- $_ := required "The reviewed Gateway namespace is required." .Values.network.gateway.namespace -}}
{{- $_ := required "The reviewed DNS namespace is required." .Values.network.dns.namespace -}}
{{- $_ := required "A single reviewed identity endpoint CIDR is required." .Values.network.identity.cidr -}}
{{- if ne .Release.Namespace "fanasa-organization-hr-development" -}}
{{- fail "Only the dedicated fanasa-organization-hr-development namespace is supported." -}}
{{- end -}}
{{- if ne .Release.Namespace .Values.targetNamespace -}}
{{- fail "Helm release namespace must equal the explicit development targetNamespace." -}}
{{- end -}}
{{- if eq .Values.persistence.webSubPath .Values.persistence.workbenchSubPath -}}
{{- fail "Web and workbench must use distinct existing PVC subPaths." -}}
{{- end -}}
{{- if or (empty .Values.network.gateway.podLabels) (empty .Values.network.dns.podLabels) -}}
{{- fail "Gateway and DNS require nonempty bounded pod label selectors." -}}
{{- end -}}
{{- if eq .Values.configuration.key .Values.configuration.metadataKey -}}
{{- fail "Runtime configuration and deployment metadata must use distinct Secret keys." -}}
{{- end -}}
{{- $releasePrefix := printf "hr-development-%s-" (.Values.sourceCommit | trunc 12) -}}
{{- if not (hasPrefix $releasePrefix .Values.release) -}}
{{- fail "Release identity must match the exact source commit prefix." -}}
{{- end -}}
{{- end -}}
