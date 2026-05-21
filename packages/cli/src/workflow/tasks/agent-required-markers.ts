export interface RequiredMarkerDefinition {
  path: string;
  markers: string[];
  onMissing?: {
    action: 'continue-session';
    maxAttempts?: number;
  };
}
