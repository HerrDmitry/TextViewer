# Bugfix Requirements Document

## Introduction

When the user scrolls horizontally past the end of a short line, that line visually disappears from the viewport. Adjacent lines squish together as if no line exists between them. The root issue is that lines whose content length is less than the horizontal scroll offset render as empty strings, which collapse to zero height in the CSS layout. Short lines scrolled past should still occupy vertical space as empty rows.

## Bug Analysis

### Current Behavior (Defect)

1.1 WHEN a line's content length is less than the horizontal column offset (startCol) THEN the system renders that line as an empty string in the viewport, which collapses to zero pixel height and visually disappears

1.2 WHEN multiple consecutive short lines are scrolled past horizontally THEN all those lines collapse, causing surrounding lines to squish together with no visual gap

### Expected Behavior (Correct)

2.1 WHEN a line's content length is less than the horizontal column offset (startCol) THEN the system SHALL render that line as an empty row that maintains the same vertical height as any other row (one line-height unit)

2.2 WHEN multiple consecutive short lines are scrolled past horizontally THEN the system SHALL preserve vertical spacing for each line, maintaining correct line count and gutter alignment

### Unchanged Behavior (Regression Prevention)

3.1 WHEN a line's content length is greater than or equal to the horizontal column offset THEN the system SHALL CONTINUE TO display the visible portion of that line starting from startCol

3.2 WHEN the horizontal column offset is zero (no horizontal scroll) THEN the system SHALL CONTINUE TO display all lines from the beginning of their content

3.3 WHEN in wrap mode THEN the system SHALL CONTINUE TO display content without horizontal scrolling (wrap mode has no startCol offset)

3.4 WHEN the viewport is vertically scrolled THEN the system SHALL CONTINUE TO show the correct subset of lines based on startLine
