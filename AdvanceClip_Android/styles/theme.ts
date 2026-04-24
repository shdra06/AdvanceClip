/**
 * FlyShelf Design System — Premium Dark Theme
 * 
 * 3-tier elevation: base → card → elevated
 * Subtle cool-blue hue shifts throughout
 * Spring physics tuned for Apple-level motion
 */

// ═══════════════════════════════════════════
// COLOR TOKENS
// ═══════════════════════════════════════════

export const colors = {
  // Background layers (subtle blue undertone gradient)
  bg: {
    base:      '#0B0D12',   // deepest background
    baseEnd:   '#0F1219',   // gradient end (slight blue shift)
    card:      '#161922',   // card surfaces
    cardHover: '#1C2029',   // card hover/active
    elevated:  '#1E222D',   // floating elements, modals
    input:     '#0E1017',   // input fields (recessed)
  },

  // Borders (semi-transparent for depth)
  border: {
    subtle:    'rgba(255,255,255,0.06)',  // default card border
    medium:    'rgba(255,255,255,0.10)',  // hover/focus border
    strong:    'rgba(255,255,255,0.15)',  // active/selected
    accent:    'rgba(99,132,255,0.25)',   // accent glow border
  },

  // Text hierarchy
  text: {
    primary:   '#F0F2F5',   // titles, important
    secondary: '#8B92A0',   // body, labels
    tertiary:  '#555C6B',   // helper, placeholder
    disabled:  '#3A3F4A',   // disabled state
  },

  // Accent palette
  accent: {
    primary:   '#6384FF',   // main brand — refined blue-violet
    primaryDim:'rgba(99,132,255,0.12)',
    success:   '#34D399',   // online, complete
    successDim:'rgba(52,211,153,0.12)',
    warning:   '#FBBF24',   // amber
    warningDim:'rgba(251,191,36,0.12)',
    error:     '#F87171',   // delete, error
    errorDim:  'rgba(248,113,113,0.12)',
    info:      '#60A5FA',   // links, info
    infoDim:   'rgba(96,165,250,0.12)',
  },

  // Semantic type colors
  type: {
    text:      '#8B92A0',
    url:       '#38BDF8',   // sky
    code:      '#34D399',   // emerald
    image:     '#A78BFA',   // violet
    pdf:       '#F87171',   // red
    doc:       '#60A5FA',   // blue
    archive:   '#FBBF24',   // amber
    video:     '#A78BFA',   // violet
    audio:     '#F472B6',   // pink
    ppt:       '#FB923C',   // orange
  },

  // Inner highlight (top edge light)
  innerHighlight: 'rgba(255,255,255,0.04)',
} as const;

// ═══════════════════════════════════════════
// SPACING SCALE (4px based)
// ═══════════════════════════════════════════

export const space = {
  xs:  4,
  sm:  8,
  md:  12,
  lg:  16,
  xl:  20,
  '2xl': 24,
  '3xl': 32,
} as const;

// ═══════════════════════════════════════════
// RADIUS
// ═══════════════════════════════════════════

export const radius = {
  sm:  8,
  md:  12,
  lg:  16,
  xl:  20,
  pill: 100,
} as const;

// ═══════════════════════════════════════════
// SHADOWS (soft, layered)
// ═══════════════════════════════════════════

export const shadows = {
  card: {
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 4 },
    shadowOpacity: 0.25,
    shadowRadius: 12,
    elevation: 4,
  },
  elevated: {
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 8 },
    shadowOpacity: 0.35,
    shadowRadius: 20,
    elevation: 8,
  },
  glow: (color: string) => ({
    shadowColor: color,
    shadowOffset: { width: 0, height: 4 },
    shadowOpacity: 0.3,
    shadowRadius: 12,
    elevation: 6,
  }),
} as const;

// ═══════════════════════════════════════════
// ANIMATION CONSTANTS
// ═══════════════════════════════════════════

export const spring = {
  /** Gentle card entrance — feels like settling into place */
  gentle: { damping: 20, stiffness: 120, mass: 0.8 },
  /** Button press — snappy but not jarring */
  press: { damping: 15, stiffness: 200, mass: 0.6 },
  /** Bounce back — confident return */
  bounce: { damping: 12, stiffness: 180, mass: 0.7 },
  /** Slow settle — modal or large element */
  slow: { damping: 25, stiffness: 90, mass: 1.0 },
} as const;

export const timing = {
  /** Card stagger base delay (ms) */
  staggerDelay: 40,
  /** Card entrance duration (ms) */
  entranceDuration: 400,
  /** Micro-interaction duration (ms) */
  microDuration: 200,
  /** Focus border transition (ms) */
  focusDuration: 250,
} as const;

// ═══════════════════════════════════════════
// TYPOGRAPHY
// ═══════════════════════════════════════════

export const font = {
  regular:   'Inter_400Regular',
  medium:    'Inter_500Medium',
  semibold:  'Inter_600SemiBold',
  bold:      'Inter_700Bold',
  extrabold: 'Inter_800ExtraBold',
} as const;

export const typography = {
  /** Screen title */
  pageTitle: {
    fontFamily: font.extrabold,
    fontSize: 30,
    letterSpacing: -0.8,
    color: colors.text.primary,
  },
  /** Section header */
  sectionTitle: {
    fontFamily: font.semibold,
    fontSize: 17,
    letterSpacing: -0.2,
    color: colors.text.primary,
  },
  /** Card title / item name */
  cardTitle: {
    fontFamily: font.semibold,
    fontSize: 15,
    letterSpacing: -0.1,
    color: colors.text.primary,
    lineHeight: 20,
  },
  /** Body text */
  body: {
    fontFamily: font.regular,
    fontSize: 14,
    color: colors.text.secondary,
    lineHeight: 20,
  },
  /** Small labels, badges */
  caption: {
    fontFamily: font.medium,
    fontSize: 11,
    letterSpacing: 0.3,
    color: colors.text.tertiary,
  },
  /** Status text, uppercase labels */
  overline: {
    fontFamily: font.semibold,
    fontSize: 10,
    letterSpacing: 1.2,
    textTransform: 'uppercase' as const,
    color: colors.text.tertiary,
  },
} as const;
