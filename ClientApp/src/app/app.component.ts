import { Component, HostListener, inject } from '@angular/core';
import { ShellStateService } from './shell/shell-state.service';
import { MenuBarComponent } from './shell/menu-bar/menu-bar.component';
import { TabContainerComponent } from './shell/tab-container/tab-container.component';
import { TextViewAreaComponent } from './shell/text-view-area/text-view-area.component';
import { StatusBarComponent } from './shell/status-bar/status-bar.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [MenuBarComponent, TabContainerComponent, TextViewAreaComponent, StatusBarComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent {
  private readonly state = inject(ShellStateService);
  readonly tabPosition = this.state.tabPosition;
  readonly errorMessage = this.state.errorMessage;

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    const isCtrlO = (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'o';
    if (!isCtrlO) return;
    event.preventDefault();
    this.state.triggerOpenFile();
  }

  dismissError(): void {
    this.state.dismissError();
  }
}
