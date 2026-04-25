// Debug Log utility for FlyShelf Android — stores recent sync events in-memory
// User can copy logs from Settings page for troubleshooting

const MAX_LOG_ENTRIES = 200;
const logEntries: string[] = [];

export const syncLog = (tag: string, message: string) => {
  const ts = new Date().toLocaleTimeString('en-GB', { hour12: false });
  const entry = `[${ts}] [${tag}] ${message}`;
  logEntries.unshift(entry); // newest first
  if (logEntries.length > MAX_LOG_ENTRIES) logEntries.length = MAX_LOG_ENTRIES;
  // Also console.log for adb logcat
  console.log(`[FlyShelf] ${entry}`);
};

export const getDebugLogs = (): string => {
  return logEntries.join('\n');
};

export const clearDebugLogs = () => {
  logEntries.length = 0;
};
