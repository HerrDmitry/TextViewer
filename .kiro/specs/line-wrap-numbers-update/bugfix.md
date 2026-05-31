# Bugfix Requirements Document

#[[file:.kiro/specs/_global/requirements-shared.md]]

## Introduction

Line numbers displayed in the gutter are out of sync while scrolling and completely broken in wrapped mode. The root cause is that the frontend computes line numbers locally from scroll position state (`startLine`, `characterOffset`) and response content parsing, rather than receiving authoritative line numbers from the backend. Race conditions between async view responses and scroll position updates cause drift in non-wrapped mode, and the content-parsing approach in wrapped mode fails when state is inconsistent.

The fix: the backend includes the line number for each returned row in both wrapped and non-wrapped view responses. The frontend displays these backend-provided line numbers directly instead of computing them on the fly.

## Bug Analysis

### Current Behavior (Defect)

1.1 WHEN the user scrolls vertically in non-wrapped mode and a get-view response arrives after the scroll position has changed again THEN the system displays line numbers computed from the stale `startLine` value that no longer matches the displayed content

1.2 WHEN the user scrolls vertically in wrapped mode THEN the system computes gutter numbers by parsing the raw response content string to detect newline boundaries, which produces incorrect line numbers when `characterOffset` or `startLine` state is inconsistent with the cached `rawResponseContent`

1.3 WHEN a wrapped-mode response contains content where a logical line wraps across more visual rows than the viewport AND the user scrolls within that line THEN the system assigns incorrect gutter numbers because `computeWrappedGutterNumbers` re-parses content from index 0 without accounting for partial-line offsets correctly in all edge cases

1.4 WHEN the viewport is resized while scrolled in wrapped mode THEN the system recomputes gutter numbers using the old `rawResponseContent` with the new `colCount`, producing misaligned line numbers until a fresh response arrives

### Expected Behavior (Correct)

2.1 WHEN a get-view response arrives in non-wrapped mode THEN the system SHALL display the line number provided by the backend for each row, regardless of the current frontend scroll position state

2.2 WHEN a get-view response arrives in wrapped mode THEN the system SHALL display the line number provided by the backend for each visual row, with the backend determining which rows get a number and which get null (continuation rows)

2.3 WHEN a wrapped-mode response contains content where a logical line wraps across more visual rows than the viewport THEN the system SHALL display the backend-provided line number on the topmost visible visual row and null for all continuation rows, as determined by the backend

2.4 WHEN the viewport is resized THEN the system SHALL request a new view from the backend (which already happens) and display the line numbers from that fresh response, ensuring line numbers always match the displayed content

### Unchanged Behavior (Regression Prevention)

3.1 WHEN the gutter is rendered THEN the system SHALL CONTINUE TO compute Gutter_Width from Total_Logical_Lines digit count multiplied by Char_Metrics width plus 16px padding

3.2 WHEN wrap mode is off THEN the system SHALL CONTINUE TO use the standard rectangular get-view request format (viewSessionId, startLine, startCol, rowCount, colCount)

3.3 WHEN wrap mode is on THEN the system SHALL CONTINUE TO use the wrapped-mode get-view request format (viewSessionId, W, startLine, characterOffset, characterCount)

3.4 WHEN the backend returns an error response THEN the system SHALL CONTINUE TO display the error message and keep previously displayed rows visible

3.5 WHEN no active tab exists or no view rows are loaded THEN the system SHALL CONTINUE TO not render the Line_Number_Gutter

3.6 WHEN the user toggles wrap mode THEN the system SHALL CONTINUE TO reset Start_Col to 0, mark non-active tabs as needing refresh, and send the appropriate view request for the active tab

3.7 WHEN scrollbar polling receives scroll-info responses THEN the system SHALL CONTINUE TO update verticalMax and horizontalMax values correctly based on scan state

---

## Bug Condition (Formal)

```pascal
FUNCTION isBugCondition(X)
  INPUT: X of type ViewResponse
  OUTPUT: boolean
  
  // Bug triggers whenever frontend-computed line numbers are used
  // (i.e., the response does NOT contain per-row line number metadata)
  RETURN X.response does NOT include per-row line number annotations
END FUNCTION
```

```pascal
// Property: Fix Checking — Backend-provided line numbers
FOR ALL X WHERE isBugCondition(X) DO
  response ← GetView'(X.request)
  ASSERT response includes line number for each row
  ASSERT displayed gutter numbers = response-provided line numbers
  ASSERT no frontend recomputation of line numbers from position state
END FOR
```

```pascal
// Property: Preservation Checking
FOR ALL X WHERE NOT isBugCondition(X) DO
  ASSERT F(X) = F'(X)
  // Gutter width, request formats, error handling, scrollbar behavior unchanged
END FOR
```
