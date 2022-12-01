﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Reflection;
using System.Diagnostics;
using OpenDentBusiness;
using CodeBase;

namespace OpenDental {
	///<summary>For SMS Text Messaging.  Used in the Text Messaging window to display an SMS message thread much like a cell phone.
	///Since users are used to seeing text message threads on cell phones, this control will be intuitive to users.</summary>
	public partial class SmsThreadView:UserControl {

		public LayoutManagerForms LayoutManager=new LayoutManagerForms();
		private List<SmsThreadMessage> _listSmsThreadMessages=null;
		///<summary>Keeps track of what page we're on.</summary>
		private int _smsThreadPage=1;
		///<summary>Set this value externally before showing the control.</summary>
		public List<SmsThreadMessage> ListSmsThreadMessages {
			get {
				return _listSmsThreadMessages;
			}
			set {
				_listSmsThreadMessages=value;
				FillMessageThread();
			}
		}
		///<summary>The number of text messages to display per page.</summary>
		public int CountMessagesToDisplay=100;
		///<summary>The list of messages to display on this page.</summary>
		private List<SmsThreadMessage> _listSmsThreadToDisplay;

		public SmsThreadView() {
			InitializeComponent();
			DoubleBuffered=true;
			SetDoubleBuffered(panelScroll,true);
		}

		private void DisposeChildrenRecursive(Control control,bool doDisposeParent) {
			if(control.HasChildren) {
				for(int i=control.Controls.Count-1;i>=0;i--) {
					ODException.SwallowAnyException(() => DisposeChildrenRecursive(control.Controls[i],doDisposeParent:true));
				}
			}
			if(doDisposeParent) {
				ODException.SwallowAnyException(() => control.Dispose());
			}
		}

		private void FillMessageThread() {
			List<Control> listControls=panelScroll.Controls.Cast<Control>().ToList();
			listControls.ForEach(x => x.Parent=null);
			Point location=panelScroll.Location;
			panelScroll.Dispose();//Clears the empty scroll space.
			panelScroll=new Panel();
			panelScroll.Location=location;
			panelScroll.Size=new Size(Width,Height-panelScroll.Location.Y);
			panelScroll.Anchor=AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;
			panelScroll.AutoScroll=true;
			LayoutManager.Add(panelScroll,this);
			Invalidate();
			if(_listSmsThreadMessages==null || _listSmsThreadMessages.Count<1) {
				listControls.ForEach(x => x.Dispose());
				panelNavigation.Visible=false;
				LayoutManager.MoveLocation(panelScroll,new Point(0,0));
				return;
			}
			BuildListMessages();
			int bodyWidth=panelScroll.Width-SystemInformation.VerticalScrollBarWidth;
			int verticalPadding=5;
			int horizontalMargin=(int)(bodyWidth*0.02);
			int y=0;
			Control controlHighlighted=null;
			panelScroll.SuspendLayout();
			//Remove message controls for messages which have been removed from the given list of messages.
			for(int i=listControls.Count-1;i>=0;i--) {
				Control control=listControls[i];//Freeze the variable so we can remove it before disposing it below.
				if(!control.Name.Contains("_")) {
					continue;
				}
				string id=control.Name.Substring(control.Name.IndexOf("_")+1);
				if(!_listSmsThreadToDisplay.Exists(x => x.ID==id)) {
					//LayoutManager.Remove() currently only supports tab pages.
					listControls.Remove(control);
					control.Dispose();//This control does not have any child controls.
				}
			}
			foreach(Control control in listControls) {
				LayoutManager.Add(control,panelScroll);
			}
			//Loop through and update existing control sizes, text, borders, etc.  Add new controls for messages not already represented.
			for(int i=0;i<_listSmsThreadToDisplay.Count;i++) {
				SmsThreadMessage msg=_listSmsThreadToDisplay[i];
				y+=verticalPadding;
				Label labelMessageHeader=new Label();
				SetDoubleBuffered(labelMessageHeader,true);
				//labelMessageHeader.MouseWheel+=MouseWheel_Scroll;//Labels automatically pass their scroll events through to their parent controls.
				labelMessageHeader.Name="labelMessageHeader_"+msg.ID;
				labelMessageHeader.Text=((msg.UserName==null)?"":(msg.UserName+"  "))+msg.MsgDateTime.ToString();
				if(msg.IsAlignedLeft) {
					labelMessageHeader.TextAlign=ContentAlignment.MiddleLeft;
				}
				else {//Aligned right
					labelMessageHeader.TextAlign=ContentAlignment.MiddleRight;
				}
				Size textSize=TextRenderer.MeasureText(labelMessageHeader.Text,panelScroll.Font,
					new Size(bodyWidth,Int32.MaxValue),TextFormatFlags.WordBreak);
				labelMessageHeader.Width=bodyWidth;
				labelMessageHeader.Height=textSize.Height+2;//Extra vertical padding to ensure that the text will fit when including the border.
				labelMessageHeader.Location=new Point(0,y);
				labelMessageHeader.Font=panelScroll.Font;
				AddOrUpdatePanelScrollChildControl(labelMessageHeader);
				y+=labelMessageHeader.Height;
				RichTextBox textBoxMessage=new RichTextBox();
				textBoxMessage.DetectUrls=true;
				textBoxMessage.ScrollBars=RichTextBoxScrollBars.None;
				textBoxMessage.LinkClicked+=TextBoxMessage_LinkClicked;
				textBoxMessage.MouseDown+=TextBoxMessage_MouseDown;
				textBoxMessage.SelectionBackColor=Color.Yellow;
				SetDoubleBuffered(textBoxMessage,true);
				textBoxMessage.MouseWheel+=MouseWheel_Scroll;//Textboxes handle their own scroll events, because they have their own scroll bars.
				textBoxMessage.Font=panelScroll.Font;
				textBoxMessage.BackColor=msg.BackColor;
				if(msg.IsImportant) {
					textBoxMessage.ForeColor=Color.Red;
				}
				textBoxMessage.Name="textSmsThreadMsg_"+msg.ID;
				textBoxMessage.BorderStyle=BorderStyle.None;
				textBoxMessage.Multiline=true;
				textBoxMessage.Text=msg.Message.Replace("\r\n","\n").Replace("\n","\r\n");//Normalize \n coming from RichTextBox to \r\n for TextBox.
				//Each message wraps horizontally.
				textSize=TextRenderer.MeasureText(textBoxMessage.Text,panelScroll.Font,
					new Size((int)(bodyWidth*0.7),Int32.MaxValue),TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
				textBoxMessage.Width=textSize.Width+4;//Extra horizontal padding to ensure that the text will fit when including the border.
				textBoxMessage.Height=textSize.Height+4;//Extra vertical padding to ensure that the text will fit when including the border.
				textBoxMessage.ReadOnly=true;
				Panel border=new Panel() {
					Width=textBoxMessage.Width+2,
					Height=textBoxMessage.Height+2,
					BackColor=Color.Black,
				};
				border.Name="msgBorder_"+msg.ID;
				if(msg.IsAlignedLeft) {
					border.Location=new Point(horizontalMargin,y);
				}
				else {//Right aligned
					border.Location=new Point(bodyWidth-horizontalMargin-border.Width,y);
				}
				textBoxMessage.Location=new Point(border.Location.X+1,border.Location.Y+1);
				textBoxMessage=(RichTextBox)AddOrUpdatePanelScrollChildControl(textBoxMessage);
				if(msg.IsHighlighted) {
					controlHighlighted=textBoxMessage;
				}
				AddOrUpdatePanelScrollChildControl(border);
				y+=border.Height;
			}
			Label labelBottomSpacer=new Label();
			SetDoubleBuffered(labelBottomSpacer,true);
			//labelBottomSpacer.MouseWheel+=MouseWheel_Scroll;//Labels automatically pass their scroll events through to their parent controls.
			labelBottomSpacer.Name="labelBottomSpacer";
			labelBottomSpacer.Width=bodyWidth;
			labelBottomSpacer.Height=verticalPadding;
			labelBottomSpacer.Location=new Point(0,y);
			AddOrUpdatePanelScrollChildControl(labelBottomSpacer);
			y+=labelBottomSpacer.Height;
			if(controlHighlighted==null) {
				controlHighlighted=labelBottomSpacer;
			}
			if(panelScroll.VerticalScroll.Value!=panelScroll.VerticalScroll.Maximum) {
				//We have to set the scroll value twice or else the scroll will occur but the UI of the scrollbar will be wrong.
				//https://stackoverflow.com/questions/5565653/scrollbar-does-not-update-on-changing-the-scroll-value
				panelScroll.VerticalScroll.Value=panelScroll.VerticalScroll.Maximum;
				panelScroll.VerticalScroll.Value=panelScroll.VerticalScroll.Maximum;//Intentionally set twice.
			}
			panelScroll.ResumeLayout();
		}

		///<summary>Returns the control which previsouly existing in the panel or returns the new control if it was added.</summary>
		private Control AddOrUpdatePanelScrollChildControl(Control control) {
			//Control name never changes once set on a control above.
			Control existingControl=panelScroll.Controls.AsEnumerable<Control>().FirstOrDefault(x => x.Name==control.Name);
			if(existingControl==null) {
				LayoutManager.Add(control,panelScroll);
				return control;
			}
			//The properties updated here must match any properties which can change from their original values above.
			//Double buffering never changes once set on a control above.
			existingControl.Text=control.Text;
			if(existingControl is Label existingLabel && control is Label label) {
				existingLabel.TextAlign=label.TextAlign;
			}
			existingControl.Width=control.Width;
			existingControl.Height=control.Height;
			existingControl.Location=control.Location;
			existingControl.ForeColor=control.ForeColor;
			control.Dispose();//This control does not have any child controls.
			LayoutManager.Move(existingControl,existingControl.Bounds);
			return existingControl;
		}

		private void TextBoxMessage_LinkClicked(object sender,LinkClickedEventArgs e) {	
			try {
				string [] links=e.LinkText.Split(new string[] {" ","\t" },StringSplitOptions.RemoveEmptyEntries);
				string link=e.LinkText;
				if(links.Length>0) {
					link=links[0];
				}
				Process.Start(link);
			}
			catch {
				MessageBox.Show(Lans.g(this,"Failed to open web browser.  Please make sure you have a default browser set and are connected to the internet then try again."),Lans.g(this,"Attention"));
			}

		}

		///<summary>Allows us to set the protected DoubleBuffered property on any Control.</summary>
		private void SetDoubleBuffered(Control control,bool isDoubleBuffered) {
			control.GetType().InvokeMember("DoubleBuffered",
				BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.SetProperty,
				null,control,new object[] { true });
		}

		private void BuildListMessages() {
			_listSmsThreadToDisplay=new List<SmsThreadMessage>();	//We'll hold what messages are to be shown in this list.
			//Sort and reverse it so the messages are in order when they're added.
			_listSmsThreadMessages=_listSmsThreadMessages.OrderByDescending(x => x.MsgDateTime).ToList();	
			int maxPage=(int)Math.Ceiling((double)_listSmsThreadMessages.Count/CountMessagesToDisplay);	//# messages per page, #/count-1=page index
			if(_smsThreadPage > maxPage) {	
				_smsThreadPage=maxPage;
			}
			labelCurrentPage.Text=(_smsThreadPage).ToString() +" "+Lan.g(this,"of")+" "+ (maxPage).ToString();
			//Here we fill the reference list that is displayed depending on which page we're on.
			int firstMessageIdx=CountMessagesToDisplay*(_smsThreadPage-1);
			int lastMessageIdx=Math.Min(CountMessagesToDisplay*_smsThreadPage,_listSmsThreadMessages.Count)-1;
			for(int i=firstMessageIdx;i<=lastMessageIdx;i++) {	
				_listSmsThreadToDisplay.Add(_listSmsThreadMessages[i]);
			}
			//Reverse order so older messages are at the top, new at the bottom.
			_listSmsThreadToDisplay=_listSmsThreadToDisplay.OrderBy(x => x.MsgDateTime).ToList();	
			if(_listSmsThreadMessages.Count<=CountMessagesToDisplay) {
				panelNavigation.Visible=false;
				LayoutManager.MoveLocation(panelScroll,new Point(0,0));
			}
			else {
				panelNavigation.Visible=true;
				LayoutManager.MoveLocation(panelScroll,new Point(0,panelNavigation.Location.Y+panelNavigation.Height));//Just below panelNavigation
			}			
			if(_smsThreadPage==maxPage) {
				butBackPage.Enabled=false;
				butEnd.Enabled=false;
				butForwardPage.Enabled=true;
				butBeginning.Enabled=true;
			}
			else if(_smsThreadPage==1) {
				butBackPage.Enabled=true;
				butEnd.Enabled=true;
				butForwardPage.Enabled=false;
				butBeginning.Enabled=false;
			}
			else {
				butBackPage.Enabled=true;
				butEnd.Enabled=true;
				butForwardPage.Enabled=true;
				butBeginning.Enabled=true;
			}
		}

		///<summary>Logic for the below method of adapting TextBox's copy method as that funcionality does not exist for RichTextBox</summary>
		private void TextBoxMessage_MouseDown(object sender,MouseEventArgs e) {
			if(!(sender is RichTextBox richTextBox)) {
				return;
			}
			if(e.Button==System.Windows.Forms.MouseButtons.Right) {
				ContextMenu contextMenu=new ContextMenu();
				MenuItem menuItem=new MenuItem("Copy");
				string text=richTextBox.SelectedText;
				if(richTextBox.SelectedText==""){
					text=richTextBox.Text;
				}
				if(text.Length==0) {
					return;
				}
				menuItem.Click+=(o,e) => ODClipboard.SetClipboard(text);
				contextMenu.MenuItems.Add(menuItem);
				richTextBox.ContextMenu=contextMenu;
			}
		}

		private void butBeginning_Click(object sender,EventArgs e) {
			if(_smsThreadPage==1) {
				return;	//Skip redrawing what we already have.
			}
			_smsThreadPage=1;
			FillMessageThread();
		}

		private void butForwardPage_Click(object sender,EventArgs e) {
			if(_smsThreadPage==1) {
				return;	//Don't go before the first page.
			}
			_smsThreadPage--;
			FillMessageThread();
		}

		private void butBackPage_Click(object sender,EventArgs e) {
			_smsThreadPage++;	//If we're on the last page, this variable will be fixed in the grid fill area.
			FillMessageThread();
		}

		private void butEnd_Click(object sender,EventArgs e) {
			_smsThreadPage=Int32.MaxValue;	//This is reset back to the maximum page, so we arrive directly at the end.
			FillMessageThread();
		}

		private void MouseWheel_Scroll(object sender,MouseEventArgs e) {
			//e.Delta is the actual scroll amount moved, despite what the comments on it say.
			int val=panelScroll.VerticalScroll.Value-e.Delta;
			if(val < panelScroll.VerticalScroll.Minimum) {
				val=panelScroll.VerticalScroll.Minimum;
			}
			else if(val > panelScroll.VerticalScroll.Maximum) {
				val=panelScroll.VerticalScroll.Maximum;
			}
			panelScroll.VerticalScroll.Value=val;
			panelScroll.PerformLayout();//Without this, setting the VerticalScroll.Value will not take effect, sometimes!
		}
	}

	public class SmsThreadMessage {
		///<summary>Context specific.  Must be a unique ID which can identify the message within the SmsThreadView control.</summary>
		public string ID;
		///<summary>The date and time the message was sent or received.</summary>
		public DateTime MsgDateTime;
		///<summary>The message itself.</summary>
		public string Message;
		///<summary>If true, the message will be left aligned.  Otherwise the message will be right aligned.  Left aligned messages will be messages from
		///the patient, and right aligned messages will be from the office.  The left/right alignment is used as a quick way to show the user who
		///wrote each part of the message thread.</summary>
		public bool IsAlignedLeft;
		///<summary>Causes the message text to show in red.</summary>
		public bool IsImportant;
		public bool IsHighlighted;
		///<summary>If not null, then shows next to the date and time.</summary>
		public string UserName;

		public Color BackColor {
			get {
				Color retVal;
				if(IsAlignedLeft) {//From Customer
					retVal=Color.FromArgb(244,255,244);
					if(IsHighlighted) {
						retVal=Color.FromArgb(220,255,220);
					}
				}
				else {//Right aligned
					retVal=Color.White;
					if(IsHighlighted) {
						retVal=Color.FromArgb(220,220,220);
					}
				}
				return retVal;
			}
		}

		public SmsThreadMessage(string id,DateTime msgDateTime,string message,bool isAlignedLeft,bool isImportant,bool isHighlighted,string userName=null) {
			ID=id;
			MsgDateTime=msgDateTime;
			Message=message;
			IsAlignedLeft=isAlignedLeft;
			IsImportant=isImportant;
			IsHighlighted=isHighlighted;
			UserName=userName;
		}

		public static int CompareMessages(SmsThreadMessage msg1,SmsThreadMessage msg2) {
			return msg1.MsgDateTime.CompareTo(msg2.MsgDateTime);
		}

	}

}
