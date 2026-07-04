namespace ReviewDojo.Core;

// Difficulty ascending. MVP seeds Mechanical..Contextual; all 5 exist in the enum.
public enum BugCategory { Mechanical, EdgeCase, Contextual, Abstraction, AgentTypical }

public enum Severity { Low, Medium, High, Critical }

public enum Verdict { Approve, RequestChanges }

public enum DifficultyTier { Easy, Medium, Hard }
