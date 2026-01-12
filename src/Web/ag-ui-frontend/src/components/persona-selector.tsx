"use client";

export interface PersonaSelectorProps {
  currentPersona?: string;
  availablePersonas: string[];
  themeColor: string;
  onPersonaChange?: (persona: string | null) => void;
}

function PersonaIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="currentColor"
      className={className}
    >
      <path
        fillRule="evenodd"
        d="M7.5 6a4.5 4.5 0 119 0 4.5 4.5 0 01-9 0zM3.751 20.105a8.25 8.25 0 0116.498 0 .75.75 0 01-.437.695A18.683 18.683 0 0112 22.5c-2.786 0-5.433-.608-7.812-1.7a.75.75 0 01-.437-.695z"
        clipRule="evenodd"
      />
    </svg>
  );
}

export function PersonaSelector({
  currentPersona,
  availablePersonas,
  themeColor,
  onPersonaChange,
}: PersonaSelectorProps) {
  return (
    <div
      style={{ backgroundColor: themeColor }}
      className="rounded-2xl shadow-xl max-w-md w-full mt-6"
    >
      <div className="bg-white/20 backdrop-blur-md p-6 rounded-2xl">
        <div className="flex items-center gap-3 mb-4">
          <div className="bg-white/30 p-2 rounded-full">
            <PersonaIcon className="w-6 h-6 text-white" />
          </div>
          <div>
            <h3 className="text-xl font-bold text-white">Speak As Ancestor</h3>
            <p className="text-white/80 text-sm">
              {currentPersona
                ? `Currently speaking as ${currentPersona}`
                : "Select an ancestor to speak as"}
            </p>
          </div>
        </div>

        {/* Current persona display */}
        {currentPersona && (
          <div className="mb-4 p-3 bg-white/20 rounded-xl">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <div className="w-8 h-8 rounded-full bg-white/30 flex items-center justify-center">
                  <span className="text-white text-sm font-bold">
                    {currentPersona.charAt(0)}
                  </span>
                </div>
                <span className="text-white font-medium">{currentPersona}</span>
              </div>
              <button
                onClick={() => onPersonaChange?.(null)}
                className="text-white/60 hover:text-white text-sm transition-colors"
              >
                Clear
              </button>
            </div>
          </div>
        )}

        {/* Available personas */}
        {availablePersonas.length > 0 ? (
          <div className="space-y-2">
            <p className="text-white/60 text-sm mb-2">Available ancestors:</p>
            {availablePersonas.map((persona) => (
              <button
                key={persona}
                onClick={() => onPersonaChange?.(persona)}
                className={`w-full p-3 rounded-xl text-left transition-all ${
                  currentPersona === persona
                    ? "bg-white/30 ring-2 ring-white"
                    : "bg-white/10 hover:bg-white/20"
                }`}
              >
                <div className="flex items-center gap-3">
                  <div className="w-8 h-8 rounded-full bg-white/30 flex items-center justify-center">
                    <span className="text-white text-sm font-bold">
                      {persona.charAt(0)}
                    </span>
                  </div>
                  <span className="text-white font-medium">{persona}</span>
                </div>
              </button>
            ))}
          </div>
        ) : (
          <div className="text-center py-4">
            <p className="text-white/60 text-sm">
              No ancestors discovered yet. Upload family content to discover
              ancestors.
            </p>
          </div>
        )}
      </div>
    </div>
  );
}
