import { Component, inject } from '@angular/core';
import { ShellStateService } from '../shell-state.service';

@Component({
  selector: 'app-tab-container',
  standalone: true,
  templateUrl: './tab-container.component.html',
  styleUrl: './tab-container.component.css'
})
export class TabContainerComponent {
  private readonly state = inject(ShellStateService);
  readonly tabs = this.state.tabs;
  readonly activeTabId = this.state.activeTabId;

  onTabClick(tabId: string): void {
    this.state.activateTab(tabId);
  }

  onCloseTab(tabId: string, event: MouseEvent): void {
    event.stopPropagation();
    this.state.closeTab(tabId);
  }
}
