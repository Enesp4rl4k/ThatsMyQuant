/** @type {import('tailwindcss').Config} */
module.exports = {
  // Both `.dark` and `.noir` activate Tailwind's dark-mode variants. Noir
  // gets extra pure-black panel overrides via index.css `html.noir` rules.
  darkMode: ["selector", ".dark, .noir"],
  content: [
    './pages/**/*.{ts,tsx}',
    './components/**/*.{ts,tsx}',
    './app/**/*.{ts,tsx}',
    './src/**/*.{ts,tsx}',
  ],
  prefix: "",
  theme: {
    container: {
      center: true,
      padding: "2rem",
      screens: {
        "2xl": "1400px",
      },
    },
    extend: {
      fontFamily: {
        // Claude design language — editorial serif headings, Inter body, JetBrains for numbers
        serif: ['Newsreader', 'Source Serif 4', 'Georgia', 'serif'],
        sans: ['Inter', 'system-ui', 'sans-serif'],
        mono: ['JetBrains Mono', 'Geist Mono', 'ui-monospace', 'monospace'],
      },
      colors: {
        border: "hsl(var(--border))",
        input: "hsl(var(--input))",
        ring: "hsl(var(--ring))",
        background: "hsl(var(--background))",
        foreground: "hsl(var(--foreground))",
        primary: {
          DEFAULT: "hsl(var(--primary))",
          foreground: "hsl(var(--primary-foreground))",
        },
        secondary: {
          DEFAULT: "hsl(var(--secondary))",
          foreground: "hsl(var(--secondary-foreground))",
        },
        destructive: {
          DEFAULT: "hsl(var(--destructive))",
          foreground: "hsl(var(--destructive-foreground))",
        },
        muted: {
          DEFAULT: "hsl(var(--muted))",
          foreground: "hsl(var(--muted-foreground))",
        },
        accent: {
          DEFAULT: "hsl(var(--accent))",
          foreground: "hsl(var(--accent-foreground))",
        },
        popover: {
          DEFAULT: "hsl(var(--popover))",
          foreground: "hsl(var(--popover-foreground))",
        },
        card: {
          DEFAULT: "hsl(var(--card))",
          foreground: "hsl(var(--card-foreground))",
        },
        // Claude palette — usable directly via bg-paper-50 / text-ink-900 / bg-coral-500
        paper: {
          50:  "#FAF9F5",
          100: "#F5F2E8",
          200: "#ECE9DD",
          300: "#DCD8C8",
        },
        ink: {
          900: "#1F1E1B",
          800: "#2C2A26",
          700: "#3D3B36",
          500: "#6B6A64",
          400: "#8A8880",
          300: "#A8A69E",
        },
        // Navy — editorial deep blue (Claude calmness, trading gravitas)
        navy: {
          50:  "#EAEEF7",
          100: "#D5DCEC",
          200: "#A6B4D2",
          400: "#3D5694",
          500: "#1E3A8A",
          600: "#172E6F",
          700: "#11224F",
          900: "#0A1733",
        },
        // Coral kept as legacy alias — points at navy so any leftover refs still work
        coral: {
          50:  "#EAEEF7",
          100: "#D5DCEC",
          200: "#A6B4D2",
          400: "#3D5694",
          500: "#1E3A8A",
          600: "#172E6F",
          700: "#11224F",
        },
        gain: {
          50:  "#E7F1EB",
          500: "#3B8C68",
          700: "#2D6A4F",
        },
        loss: {
          50:  "#F7E5E8",
          500: "#C9485F",
          700: "#B5364B",
        },
        warm: {
          line: "rgba(31, 30, 27, 0.08)",
          line2: "rgba(31, 30, 27, 0.12)",
        },
      },
      borderRadius: {
        lg: "var(--radius)",
        md: "calc(var(--radius) - 2px)",
        sm: "calc(var(--radius) - 4px)",
      },
      boxShadow: {
        // Claude shadows are subtle — single soft layer, no glow
        'paper': '0 1px 2px rgba(31, 30, 27, 0.04), 0 1px 3px rgba(31, 30, 27, 0.06)',
        'paper-lg': '0 2px 8px rgba(31, 30, 27, 0.06), 0 8px 24px rgba(31, 30, 27, 0.06)',
      },
      keyframes: {
        "accordion-down": {
          from: { height: "0" },
          to: { height: "var(--radix-accordion-content-height)" },
        },
        "accordion-up": {
          from: { height: "var(--radix-accordion-content-height)" },
          to: { height: "0" },
        },
      },
      animation: {
        "accordion-down": "accordion-down 0.2s ease-out",
        "accordion-up": "accordion-up 0.2s ease-out",
      },
    },
  },
  plugins: [require("tailwindcss-animate")],
}
